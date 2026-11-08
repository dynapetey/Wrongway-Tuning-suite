"""Independent calibration editing engine. No hardware access or licensing."""
from dataclasses import dataclass, asdict, fields
import ast
import csv
import io
import json
import math
import os
from pathlib import Path
import shutil
import struct
import tempfile
from datetime import datetime, timezone
import xml.etree.ElementTree as ET


def number(value):
    text = str(value).strip()
    return int(text, 16) if text.lower().startswith('0x') else int(text)


def finite(value):
    value = float(value)
    if not math.isfinite(value):
        raise ValueError('Enter a finite number.')
    return value


@dataclass
class Parameter:
    Title: str = 'New parameter'
    Group: str = 'Custom'
    Units: str = ''
    Address: str = '0x0'
    Storage: str = 'Unsigned'
    ByteWidth: int = 1
    LittleEndian: bool = False
    Bit: int = 0
    Rows: int = 1
    Columns: int = 1
    Scale: float = 1.0
    Offset: float = 0.0
    Minimum: float | None = None
    Maximum: float | None = None
    RowLabels: list | None = None
    ColumnLabels: list | None = None
    Notes: str = ''
    RowStride: int = 0
    ColumnStride: int = 0

    @classmethod
    def from_dict(cls, data):
        known = {f.name for f in fields(cls)}
        unknown = set(data) - known
        if unknown:
            raise ValueError('Unknown definition fields: ' + ', '.join(sorted(unknown)))
        return cls(**data)

    @property
    def address(self):
        return number(self.Address)

    @property
    def col_stride(self):
        return self.ColumnStride or self.ByteWidth

    @property
    def row_stride(self):
        return self.RowStride or self.Columns * self.col_stride

    def validate(self, length):
        if not self.Title.strip():
            raise ValueError('A parameter needs a title.')
        if self.Storage not in ('Unsigned', 'Signed', 'Float', 'Flag'):
            raise ValueError('Unknown storage type.')
        for key in ('ByteWidth', 'Bit', 'Rows', 'Columns', 'RowStride', 'ColumnStride'):
            if type(getattr(self, key)) is not int:
                raise ValueError(key + ' must be an integer.')
        if type(self.LittleEndian) is not bool:
            raise ValueError('LittleEndian must be true or false.')
        if self.ByteWidth not in ((4, 8) if self.Storage == 'Float' else (1, 2, 4)):
            raise ValueError('Unsupported storage width.')
        if not (1 <= self.Rows <= 512 and 1 <= self.Columns <= 128 and self.Rows * self.Columns <= 65536):
            raise ValueError('Table dimensions exceed supported limits.')
        if self.Storage == 'Flag' and (self.Rows != 1 or self.Columns != 1 or not 0 <= self.Bit < self.ByteWidth * 8):
            raise ValueError('A flag needs one cell and a valid bit number.')
        if self.ColumnStride < 0 or self.RowStride < 0 or self.col_stride < self.ByteWidth or self.row_stride < self.Columns * self.col_stride:
            raise ValueError('Strides must not overlap cells.')
        if finite(self.Scale) == 0:
            raise ValueError('Scale cannot be zero.')
        finite(self.Offset)
        if self.Minimum is not None:
            finite(self.Minimum)
        if self.Maximum is not None:
            finite(self.Maximum)
        if self.Minimum is not None and self.Maximum is not None and self.Minimum > self.Maximum:
            raise ValueError('Minimum exceeds maximum.')
        end = self.address + (self.Rows - 1) * self.row_stride + (self.Columns - 1) * self.col_stride + self.ByteWidth
        if self.address < 0 or end > length:
            raise ValueError('Parameter extends outside the loaded BIN.')


class TuneImage:
    def __init__(self, data):
        self.original = bytes(data)
        self.data = bytearray(data)
        self.undo_stack = []
        self.redo_stack = []

    @property
    def changed_bytes(self):
        return sum(a != b for a, b in zip(self.original, self.data))

    def cell_address(self, p, row=0, col=0):
        p.validate(len(self.data))
        if not (0 <= row < p.Rows and 0 <= col < p.Columns):
            raise ValueError('Cell is outside this table.')
        return p.address + row * p.row_stride + col * p.col_stride

    def read(self, p, row=0, col=0):
        at = self.cell_address(p, row, col)
        raw = self.data[at:at + p.ByteWidth]
        if p.Storage == 'Float':
            value = struct.unpack(('<' if p.LittleEndian else '>') + ('f' if p.ByteWidth == 4 else 'd'), raw)[0]
        else:
            value = int.from_bytes(raw, 'little' if p.LittleEndian else 'big', signed=p.Storage == 'Signed')
            if p.Storage == 'Flag':
                return float(bool(value & (1 << p.Bit)))
        return finite(value * p.Scale + p.Offset)

    def encode(self, p, row, col, value):
        at = self.cell_address(p, row, col)
        value = finite(value)
        if (p.Minimum is not None and value < p.Minimum) or (p.Maximum is not None and value > p.Maximum):
            raise ValueError('Value is outside the configured limits.')
        order = 'little' if p.LittleEndian else 'big'
        if p.Storage == 'Flag':
            if value not in (0, 1):
                raise ValueError('A switch accepts 0 or 1.')
            old = int.from_bytes(self.data[at:at + p.ByteWidth], order)
            new = old | (1 << p.Bit) if value else old & ~(1 << p.Bit)
            result = new.to_bytes(p.ByteWidth, order)
        else:
            raw = finite((value - p.Offset) / p.Scale)
            if p.Storage == 'Float':
                try:
                    result = struct.pack(('<' if p.LittleEndian else '>') + ('f' if p.ByteWidth == 4 else 'd'), raw)
                except (OverflowError, struct.error) as exc:
                    raise ValueError('Value exceeds the float storage range.') from exc
            else:
                rounded = math.floor(raw + .5) if raw >= 0 else math.ceil(raw - .5)
                try:
                    result = rounded.to_bytes(p.ByteWidth, order, signed=p.Storage == 'Signed')
                except OverflowError as exc:
                    raise ValueError('Value exceeds the integer storage range.') from exc
        return at, result

    def replace(self, at, data):
        self.commit([(at, bytes(data))])

    def commit(self, changes):
        changes = list(changes)
        occupied = set()
        for at, data in changes:
            if at < 0 or at + len(data) > len(self.data):
                raise ValueError('Write extends outside the loaded BIN.')
            cells = set(range(at, at + len(data)))
            if cells & occupied:
                raise ValueError('Batch contains overlapping writes.')
            occupied.update(cells)
        entries = [(at, bytes(self.data[at:at + len(data)]), data) for at, data in changes if self.data[at:at + len(data)] != data]
        if not entries:
            return
        for at, _, after in entries:
            self.data[at:at + len(after)] = after
        self.undo_stack.append(entries)
        self.redo_stack.clear()

    def write(self, p, row, col, value):
        self.commit([self.encode(p, row, col, value)])
        return self.read(p, row, col)

    def batch(self, p, edits):
        self.commit([self.encode(p, row, col, value) for row, col, value in edits])

    def undo(self):
        if self.undo_stack:
            entries = self.undo_stack.pop()
            for at, before, _ in entries:
                self.data[at:at + len(before)] = before
            self.redo_stack.append(entries)

    def redo(self):
        if self.redo_stack:
            entries = self.redo_stack.pop()
            for at, _, after in entries:
                self.data[at:at + len(after)] = after
            self.undo_stack.append(entries)

    def cell_changed(self, p, row, col):
        at = self.cell_address(p, row, col)
        return self.data[at:at + p.ByteWidth] != self.original[at:at + p.ByteWidth]


def atomic_save(path, data):
    path = Path(path).expanduser().absolute()
    backup = None
    fd, temp = tempfile.mkstemp(prefix='.wrongway-', dir=path.parent)
    try:
        with os.fdopen(fd, 'wb') as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        if path.exists():
            stamp = datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S-%f')
            backup = path.with_name(path.name + '.' + stamp + '.bak')
            shutil.copy2(path, backup)
        os.replace(temp, path)
        dfd = os.open(path.parent, os.O_DIRECTORY)
        try:
            os.fsync(dfd)
        finally:
            os.close(dfd)
    finally:
        if os.path.exists(temp):
            os.unlink(temp)
    return backup


def load_definitions(path, length):
    result = json.loads(Path(path).read_text())
    if result.get('FormatVersion', 1) != 1:
        raise ValueError('Unsupported definition version.')
    params = [Parameter.from_dict(p) for p in result.get('Parameters', [])]
    for p in params:
        p.validate(length)
    if not params:
        raise ValueError('The definition file contains no parameters.')
    return result, params


def save_definitions(path, title, expected_os, params):
    data = {'FormatVersion': 1, 'Title': title, 'ExpectedOS': str(expected_os), 'Parameters': [asdict(p) for p in params]}
    return atomic_save(path, (json.dumps(data, indent=2, allow_nan=False) + '\n').encode())


def affine(expression):
    """Resolve a linear conversion without executing input code."""
    def visit(node):
        if isinstance(node, ast.Expression):
            return visit(node.body)
        if isinstance(node, ast.Name) and node.id.upper() == 'X':
            return 1., 0.
        if isinstance(node, ast.Constant) and type(node.value) in (int, float):
            return 0., finite(node.value)
        if isinstance(node, ast.UnaryOp) and isinstance(node.op, (ast.UAdd, ast.USub)):
            a, b = visit(node.operand)
            sign = -1 if isinstance(node.op, ast.USub) else 1
            return a * sign, b * sign
        if isinstance(node, ast.BinOp):
            a, b = visit(node.left)
            c, d = visit(node.right)
            if isinstance(node.op, ast.Add):
                return a + c, b + d
            if isinstance(node.op, ast.Sub):
                return a - c, b - d
            if isinstance(node.op, ast.Mult) and (a == 0 or c == 0):
                return a * d + c * b, b * d
            if isinstance(node.op, ast.Div) and c == 0 and d != 0:
                return a / d, b / d
        raise ValueError('Only invertible linear expressions in X are supported for editable values.')
    if len(expression) > 4096:
        raise ValueError('Equation is too long.')
    a, b = visit(ast.parse(expression.strip(), mode='eval'))
    if not finite(a) or not math.isfinite(b):
        raise ValueError('Conversion is not invertible.')
    return a, b


def import_xdf(path, length):
    data = Path(path).read_bytes()
    if len(data) > 32 * 1024 * 1024 or b'<!DOCTYPE' in data.upper() or b'<!ENTITY' in data.upper() or b'\x00' in data:
        raise ValueError('XDF must be UTF-8 XML without DTDs or external entities, at most 32 MiB.')
    root = ET.fromstring(data)
    if root.tag != 'XDFFORMAT':
        raise ValueError('Not a TunerPro XDF file.')
    header = root.find('XDFHEADER')
    base = root.find('XDFHEADER/BASEOFFSET')
    base_offset = 0 if base is None else number(base.get('offset', 0)) * (-1 if number(base.get('subtract', 0)) else 1)
    title = root.findtext('XDFHEADER/deftitle', 'Imported XDF')
    cats = {} if header is None else {number(c.get('index', 0)) + 1: c.get('name', 'Custom') for c in header.findall('CATEGORY')}
    params, warnings = [], []
    for item in root:
        if item.tag == 'XDFHEADER' or not item.tag.startswith('XDF'):
            continue
        name = item.findtext('title', item.tag)
        try:
            if item.tag not in ('XDFCONSTANT', 'XDFFLAG', 'XDFTABLE'):
                raise ValueError('Unsupported entry type ' + item.tag)
            owner = item.find("XDFAXIS[@id='z']") if item.tag == 'XDFTABLE' else item
            embedded = None if owner is None else owner.find('EMBEDDEDDATA')
            if embedded is None or 'mmedaddress' not in embedded.attrib:
                raise ValueError('Missing value storage address.')
            bits = number(embedded.get('mmedelementsizebits', 8))
            flags = number(embedded.get('mmedtypeflags', 0))
            if bits not in (8, 16, 32, 64) or flags & ~0x10007:
                raise ValueError('Unsupported width or storage flags.')
            p = Parameter(Title=name, Address=hex(number(embedded.get('mmedaddress')) + base_offset),
                          ByteWidth=bits // 8, LittleEndian=bool(flags & 2),
                          Storage='Flag' if item.tag == 'XDFFLAG' else 'Float' if flags & 0x10000 else 'Signed' if flags & 1 else 'Unsigned',
                          Units=owner.findtext('units', item.findtext('units', '')), Notes=item.findtext('description', ''))
            major, minor = number(embedded.get('mmedmajorstridebits', 0)), number(embedded.get('mmedminorstridebits', 0))
            if major not in (0, bits) or minor != 0:
                raise ValueError('XDF stride orientation is unsupported; define RowStride/ColumnStride explicitly in JSON.')
            category = item.find('CATEGORYMEM')
            if category is not None:
                p.Group = cats.get(number(category.get('category', 0)), 'Custom')
            if p.Storage == 'Flag':
                mask = number(item.findtext('mask', item.findtext('bitmask', '0')))
                if mask <= 0 or mask & (mask - 1):
                    raise ValueError('A switch requires a single-bit mask.')
                p.Bit = mask.bit_length() - 1
            else:
                maths = owner.findall('MATH')
                if len(maths) > 1 or any('row' in m.attrib or 'col' in m.attrib for m in maths):
                    raise ValueError('Per-cell equations are unsupported.')
                if maths:
                    for var in maths[0].findall('VAR'):
                        if var.get('id', '').upper() != 'X' or var.get('type', '0') != '0' or list(var):
                            raise ValueError('Linked variables are unsupported.')
                p.Scale, p.Offset = affine(maths[0].get('equation', 'X') if maths else 'X')
            if item.tag == 'XDFTABLE':
                p.Rows, p.Columns = number(embedded.get('mmedrowcount', 1)), number(embedded.get('mmedcolcount', 1))
                for axis, count, prop in [('x', p.Columns, 'ColumnLabels'), ('y', p.Rows, 'RowLabels')]:
                    labels = item.findall(f"XDFAXIS[@id='{axis}']/LABEL")
                    if labels:
                        values = [str(i) for i in range(count)]
                        for label in labels:
                            index = number(label.get('index', -1))
                            if 0 <= index < count:
                                values[index] = label.get('value', str(index))
                        setattr(p, prop, values)
                    else:
                        warnings.append(f'{name}: {axis} axis labels omitted; displaying indices.')
            p.validate(length)
            params.append(p)
        except (ValueError, SyntaxError, TypeError, OverflowError) as exc:
            warnings.append(f'{name}: skipped — {exc}')
    if not params:
        raise ValueError('No supported parameters found. ' + ' '.join(warnings[:4]))
    return title, params, warnings


SEGMENT_NAMES = ['Operating system', 'Engine calibration', 'Engine diagnostics', 'Transmission calibration', 'Transmission diagnostics', 'Fuel system', 'System', 'Speedometer']


def pcm_layout(data):
    size = len(data)
    controller = 'P59' if size == 1048576 else 'P01' if size == 524288 else None
    if controller is None or data[0x1fffe:0x20000] != b'\x4a\xfc' or data[-2:] != b'\x4a\xfc':
        raise ValueError('Image is not a recognized P01/P59 BIN.')
    if controller == 'P01' and (data[0x3ffc:0x4000] == b'\x00\x00\x00\x00' or (data[0x20002:0x20004] == b'\x00\x01' and data[0x2000a:0x2000c] == b'\x01\x00')):
        raise ValueError('Ambiguous 512 KiB image; BlackBox/E54 images are not supported here.')
    osid = int.from_bytes(data[0x504:0x508], 'big')
    if osid in (0, 0xffffffff):
        raise ValueError('Invalid operating system ID.')
    segments = []
    for i, name in enumerate(SEGMENT_NAMES):
        start, end = struct.unpack_from('>II', data, 0x50c + i * 8)
        storage = 0x500 if start == 0 else start
        begin = start if start == 0 else start + 2
        if start % 2 or end % 2 != 1 or start >= end or end >= size or storage + 2 > size:
            raise ValueError(f'Corrupt segment range: {name}.')
        segments.append({'name': name, 'start': start, 'end': end, 'begin': begin, 'storage': storage})
    if segments[0]['start'] != 0 or segments[0]['end'] != size - 1:
        raise ValueError('Unsupported operating system checksum range.')
    # Calibration ranges must be ordered and disjoint, outside the boot/header area.
    for i, segment in enumerate(segments[1:], 1):
        if segment['start'] < 0x8000:
            raise ValueError('Unsupported calibration segment layout.')
        for previous in segments[1:i]:
            if max(segment['start'], previous['start']) <= min(segment['end'], previous['end']):
                raise ValueError('Calibration segment ranges overlap.')
    return controller, osid, segments


def segment_checksum(data, segment):
    total, at = 0, segment['begin']
    while at <= segment['end']:
        if at == segment['storage']:
            at += 2
            continue
        if at == 0x4000:
            at = 0x20000
            if at > segment['end']:
                break
        total += (data[at] << 8) | data[at + 1]
        at += 2
    return (-total) & 0xffff


def checksum_report(data):
    controller, osid, segments = pcm_layout(data)
    for s in segments:
        s['stored'] = int.from_bytes(data[s['storage']:s['storage'] + 2], 'big')
        s['needed'] = segment_checksum(data, s)
        s['valid'] = s['stored'] == s['needed']
    return controller, osid, segments


def repair_checksums(image):
    _, _, segments = pcm_layout(image.data)
    candidate = bytearray(image.data)
    # Repair contained calibration segments first, then the overall OS range.
    for s in segments[1:] + segments[:1]:
        at = s['storage']
        candidate[at:at + 2] = segment_checksum(candidate, s).to_bytes(2, 'big')
    if not all(s['valid'] for s in checksum_report(candidate)[2]):
        raise ValueError('Checksums could not be repaired consistently.')
    image.commit([(s['storage'], bytes(candidate[s['storage']:s['storage'] + 2])) for s in segments])


def export_table(image, p):
    stream = io.StringIO()
    writer = csv.writer(stream)
    for row in range(p.Rows):
        writer.writerow([format(image.read(p, row, col), '.10g') for col in range(p.Columns)])
    return stream.getvalue()


def parse_table(text, p):
    delimiter = '\t' if '\t' in text else ','
    rows = list(csv.reader(io.StringIO(text.strip()), delimiter=delimiter))
    if len(rows) != p.Rows or any(len(row) != p.Columns for row in rows):
        raise ValueError(f'Expected exactly {p.Rows} rows and {p.Columns} columns.')
    return [(r, c, finite(value)) for r, row in enumerate(rows) for c, value in enumerate(row)]
