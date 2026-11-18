"""CSV math, VCM telemetry window functions, and histogram tools. User expressions are parsed, never eval'd."""
import ast
from bisect import bisect_left, bisect_right
import csv
import math
from pathlib import Path
import re
import xml.etree.ElementTree as ET
from .core import finite

AFR_EXPRESSION = '(([7101.10]/.5)+9.57)'

VCM_TELEMETRY_PRESETS = {
    'Wideband AFR': AFR_EXPRESSION,
    'CL Filter': '[2517.161.avg(1500)]=0 and [2517.161.avg(-200)]=0 and (abs([50090.156.slope(1500)])+abs([50090.156.slope(-500)]))<2 and [50118.238] = 1.00',
    'PE Filter': '[2517.161.avg(1500)]=0 and [2517.161.avg(-200)]=0 and (abs([50090.156.slope(250)])+abs([50090.156.slope(-250)]))<2 and [50118.238.avg(250)] < 1.00',
    'MAF CL Error': '(([2320.71]+([2320.71]*([50116.156]+[50114.156])/100))-[50040.71])/[50040.71]*100',
    'MAF PE Error': '(([2320.71]+([2320.71]*([50119.238]-[50118.238])/[50118.238]))-[50040.71])/[50040.71]*100',
    'PE FOR AFR Error': '(([2320.71]+([2320.71]*(([6001]-[50121])/[50121])))-[16.71])/[16.71]*100',
    'VVE FOR CL Error': '(([2320.71]+([2320.71]*([50116.156]+[50114.156])/100))-[2311.71])/[2311.71]*100',
    'VVE FOR PE Error': '(([2320.71]+([2320.71]*([50119.238]-[50118.238])/[50118.238]))-[2311.71])/[2311.71]*100',
    'VVE FOR AFR Error': '(([2320.71]+([2320.71]*(([50120]-[50121])/[50121])))-[2311.71])/[2311.71]*100',
    'Combined Fuel Trim': '[50116.156] + [50114.156]',
    'Boost Pressure (psi)': '([50030.56] - 101.325) * 0.1450377',
    'Injector Duty Cycle': '([6200.127] * [50070.161]) / 1200',
    'Dynamic Air Mass Per Sec': '[2321.71] * [50070.161] / 15',
    'Horsepower (Estimated)': '([13.91] * 0.44704) * ([13.91.derivative()] * 0.44704) * 1814 / 745.7',
    'Torque (Estimated)': '(([13.91] * 0.44704) * ([13.91.derivative()] * 0.44704) * 1814 / 745.7) * 5252 / max([12.161], 500)',
}

VCM_PID_ALIASES = {
    '3': ['Fuel System Status', 'Fueling Mode'],
    '4': ['Engine Load'],
    '5': ['Coolant Temperature', 'ECT Sensor', 'ECT'],
    '6': ['Short-Term Fuel Trim B1', 'Short Term FT Left Bank', 'STFT B1'],
    '8': ['Short-Term Fuel Trim B2', 'Short Term FT Right Bank', 'STFT B2'],
    '7': ['Long-Term Fuel Trim B1', 'Long Term FT Left Bank', 'LTFT B1'],
    '9': ['Long-Term Fuel Trim B2', 'Long Term FT Right Bank', 'LTFT B2'],
    '11': ['Manifold Pressure', 'MAP Sensor', 'MAP'],
    '12': ['Engine RPM', 'Engine Speed', 'RPM'],
    '13': ['Vehicle Speed', 'Vehicle Speed Sensor', 'VSS'],
    '14': ['Spark Advance', 'Ignition Timing'],
    '15': ['Intake Air Temperature', 'IAT Sensor', 'IAT'],
    '16': ['MAF Airflow', 'MAF Sensor', 'MAF'],
    '17': ['Throttle Position', 'TP Sensor', 'TPS'],
    '2100': ['Module Voltage', 'Ignition 1 Signal'],
    '2210': ['Desired Idle Speed'],
    '2220': ['Desired Idle Airflow', 'Desired IAC Airflow'],
    '2301': ['MAF Frequency', 'MAF Sensor Frequency'],
    '2311': ['Dynamic Airflow', 'MAF Rationality Test - SpeedDensity', 'MAF Airflow'],
    '2320': ['Dynamic Cylinder Air / Airflow', 'MAF Airflow', 'Load'],
    '2321': ['Cylinder Air', 'Load'],
    '2517': ['DFCO Active', 'Decel'],
    '2630': ['Knock Retard', 'Knock Retard Degrees'],
    '6001': ['Commanded AFR', 'Target A/F Ratio'],
    '6010': ['Commanded Equivalence Ratio', 'Target Equivalence Ratio'],
    '6200': ['Injector Pulse Width B1', 'Injector PWM Left Bank Average'],
    '6201': ['Injector Pulse Width B2', 'Injector PWM Right Bank Average'],
    '7101': ['7101.10', 'A/C Pressure Sensor Input', 'A/C High Side Pressure Sensor'],
    '50010': ['Coolant Temperature', 'ECT Sensor', 'ECT'],
    '50011': ['Intake Air Temperature', 'IAT Sensor', 'IAT'],
    '50020': ['Vehicle Speed', 'Vehicle Speed Sensor', 'VSS'],
    '50030': ['Manifold Pressure', 'MAP Sensor', 'MAP'],
    '50040': ['MAF Airflow', 'MAF Sensor', 'MAF'],
    '50041': ['Cylinder Air', 'Load'],
    '50050': ['Module Voltage', 'Ignition 1 Signal'],
    '50070': ['Engine RPM', 'Engine Speed', 'RPM'],
    '50080': ['MAF Frequency', 'MAF Sensor Frequency'],
    '50090': ['Throttle Position', 'TP Sensor', 'TPS'],
    '50091': ['Accelerator Pedal Position', 'Throttle Position', 'TPS'],
    '50110': ['Spark Advance', 'Ignition Timing'],
    '50111': ['Knock Retard', 'Knock Retard Degrees'],
    '50114': ['Long-Term Fuel Trim B1', 'Long Term FT Left Bank', 'LTFT B1'],
    '50116': ['Short-Term Fuel Trim B1', 'Short Term FT Left Bank', 'STFT B1'],
    '50118': ['Commanded Equivalence Ratio', 'Target Equivalence Ratio'],
    '50119': ['Wideband Equivalence Ratio', 'Wideband AFR', 'AFR'],
    '50120': ['Wideband AFR', 'AFR', 'A/C Wideband A/F Ratio'],
    '50121': ['Commanded AFR', 'Target A/F Ratio'],
}

SAFE_FUNCTIONS = {
    'abs': (1, abs),
    'sqrt': (1, math.sqrt),
    'log': (1, math.log),
    'ln': (1, math.log),
    'log10': (1, math.log10),
    'exp': (1, math.exp),
    'round': (1, round),
    'floor': (1, math.floor),
    'ceil': (1, math.ceil),
    'sin': (1, math.sin),
    'cos': (1, math.cos),
    'tan': (1, math.tan),
    'min': (2, min),
    'max': (2, max),
    'pow': (2, math.pow),
    'if': (3, lambda cond, t, f: t if cond != 0 else f),
    'iff': (3, lambda cond, t, f: t if cond != 0 else f),
    'clamp': (3, lambda val, lo, hi: max(lo, min(hi, val))),
}

WINDOW_METHOD_RE = re.compile(
    r'^(?P<base>.+)\.(?P<op>avg|mean|min|max|sum|count|slope|rate|derivative|deriv|roc|integral|integ|delta|diff|lag|delay|prev)\(\s*(?P<arg>[+-]?(?:\d+(?:\.\d*)?|\.\d+))?\s*\)$',
    re.IGNORECASE,
)


def _normalize_expression(expression):
    text = (expression or '').strip()
    text = text.replace('&lt;', '<').replace('&gt;', '>').replace('&amp;', '&')
    text = re.sub(r'(\d+(?:\.\d+)?)%', r'\1', text)
    text = re.sub(r'(?<![<>!=])=(?!=)', '==', text)
    text = text.replace('<>', '!=')
    return text


def parse_token_spec(token):
    token = token.strip()
    m = WINDOW_METHOD_RE.match(token)
    if not m:
        return token, 'value', 0.0
    base = m.group('base').strip()
    op = m.group('op').lower()
    arg = m.group('arg')
    win = float(arg) if arg else (
        250.0 if op in ('derivative', 'deriv', 'roc') else
        500.0 if op in ('slope', 'rate', 'avg', 'mean', 'min', 'max') else
        100.0 if op in ('delta', 'diff', 'lag', 'delay', 'prev') else 0.0
    )
    return base, op, win


def resolve_column_name(base_token, headers):
    if base_token in headers:
        return base_token
    parts = [p.strip() for p in base_token.split('|', 1)]
    name_part = parts[0]
    for h in headers:
        hl = h.lower()
        if hl == base_token.lower() or hl == name_part.lower():
            return h
        if hl.startswith(name_part.lower() + ' [') or hl.startswith(name_part.lower() + ' ('):
            return h
    pid_part = name_part.split('.', 1)[0]
    if pid_part in VCM_PID_ALIASES:
        for candidate in VCM_PID_ALIASES[pid_part]:
            cl = candidate.lower()
            for h in headers:
                hl = h.lower()
                if hl == cl or hl.startswith(cl + ' [') or hl.startswith(cl + ' ('):
                    return h
    return base_token


def parse_math(expression):
    aliases = {}

    def alias(match):
        name = 'column_' + str(len(aliases))
        aliases[name] = match.group(1)
        return name

    normalized = _normalize_expression(expression)
    text = re.sub(r'\[([^\]]+)\]', alias, normalized)
    if len(text) > 4096:
        raise ValueError('Formula is too long.')
    tree = ast.parse(text.strip(), mode='eval')
    if sum(1 for _ in ast.walk(tree)) > 250:
        raise ValueError('Formula is too complex.')
    allowed_nodes = (
        ast.Expression, ast.Constant, ast.Name, ast.Load,
        ast.BinOp, ast.UnaryOp, ast.BoolOp, ast.Compare, ast.Call,
        ast.Add, ast.Sub, ast.Mult, ast.Div, ast.Mod,
        ast.UAdd, ast.USub, ast.Not,
        ast.And, ast.Or,
        ast.Eq, ast.NotEq, ast.Lt, ast.LtE, ast.Gt, ast.GtE,
    )
    for node in ast.walk(tree):
        if not isinstance(node, allowed_nodes):
            raise ValueError('Use numbers, [column names], +, -, *, /, comparisons, and supported VCM math functions.')
        if isinstance(node, ast.Call):
            if not isinstance(node.func, ast.Name) or node.func.id.lower() not in SAFE_FUNCTIONS:
                raise ValueError('Unsupported function in formula.')
            arity, _ = SAFE_FUNCTIONS[node.func.id.lower()]
            if len(node.args) != arity or node.keywords:
                raise ValueError(f'Function {node.func.id} requires {arity} arguments.')
        elif isinstance(node, ast.Name) and node.id not in aliases and node.id.lower() not in SAFE_FUNCTIONS:
            raise ValueError('Surround a column name with [brackets].')
        if isinstance(node, ast.Constant) and type(node.value) not in (int, float):
            raise ValueError('Only numeric constants are supported.')
    return tree, aliases


def _extract_row_time_ms(row, index):
    for key in ('Time (s)', 'Time', 'Seconds'):
        if key in row and row[key] != '':
            try:
                return float(row[key]) * 1000.0
            except (ValueError, TypeError):
                pass
    if 'Time (ms)' in row and row['Time (ms)'] != '':
        try:
            return float(row['Time (ms)'])
        except (ValueError, TypeError):
            pass
    return index * 100.0


def _evaluate_window_spec(raw_token, headers, rows, row_index, times_ms):
    base, op, win_ms = parse_token_spec(raw_token)
    col = resolve_column_name(base, headers)
    if op == 'value':
        return finite(rows[row_index][col])

    now_t = times_ms[row_index]
    if op in ('lag', 'delay', 'prev'):
        target = now_t - (1.0 if win_ms == 0 else abs(win_ms))
        best = bisect_right(times_ms, target) - 1
        if best < 0:
            raise ValueError('No earlier sample in the log.')
        return finite(rows[best][col])

    if op in ('delta', 'diff'):
        cur_val = finite(rows[row_index][col])
        target = now_t - (1.0 if win_ms == 0 else abs(win_ms))
        best = bisect_right(times_ms, target) - 1
        if best < 0:
            raise ValueError('No earlier sample in the log.')
        return finite(cur_val - finite(rows[best][col]))

    if op in ('integral', 'integ'):
        start_t = times_ms[0] if win_ms == 0 else (now_t - win_ms if win_ms > 0 else now_t)
        end_t = now_t if win_ms >= 0 else now_t + abs(win_ms)
        accum = 0.0
        first = max(0, bisect_right(times_ms, start_t) - 1)
        last = min(len(rows) - 1, bisect_left(times_ms, end_t))
        for i in range(first, last):
            t0, t1 = times_ms[i], times_ms[i + 1]
            if t1 <= start_t or t0 >= end_t:
                continue
            try:
                v0, v1 = finite(rows[i][col]), finite(rows[i + 1][col])
                dt = (min(end_t, t1) - max(start_t, t0)) / 1000.0
                if dt > 0:
                    slope = (v1 - v0) / (t1 - t0)
                    left = v0 + slope * (max(start_t, t0) - t0)
                    right = v0 + slope * (min(end_t, t1) - t0)
                    accum += 0.5 * (left + right) * dt
            except (ValueError, KeyError, TypeError):
                continue
        return finite(accum)

    # HP Tuners VCM Scanner convention: positive ms = trailing window [now - ms, now]; negative ms = lookahead window [now, now + |ms|]
    start_t = now_t - win_ms if win_ms >= 0 else now_t
    end_t = now_t if win_ms >= 0 else now_t + abs(win_ms)
    if end_t < start_t:
        start_t, end_t = end_t, start_t

    samples = []
    for i in range(bisect_left(times_ms, start_t), bisect_right(times_ms, end_t)):
        try:
            samples.append((times_ms[i], finite(rows[i][col])))
        except (ValueError, KeyError, TypeError):
            continue
    if not samples:
        samples = [(now_t, finite(rows[row_index][col]))]

    if op in ('slope', 'rate', 'derivative', 'deriv', 'roc'):
        if len(samples) < 2 and row_index > 0:
            try:
                samples.insert(0, (times_ms[row_index - 1], finite(rows[row_index - 1][col])))
            except (ValueError, KeyError, TypeError):
                pass
        t0, y0 = samples[0]
        t1, y1 = samples[-1]
        if t1 == t0:
            return 0.0
        return finite((y1 - y0) / ((t1 - t0) / 1000.0))

    vals = [v for _, v in samples]
    if op in ('avg', 'mean'):
        return finite(sum(vals) / len(vals))
    if op == 'min':
        return finite(min(vals))
    if op == 'max':
        return finite(max(vals))
    if op == 'sum':
        return finite(sum(vals))
    if op == 'count':
        return float(len(vals))
    return finite(vals[-1])


def calculate(expression, row, rows=None, row_index=0, times_ms=None, headers=None):
    return _calculate(parse_math(expression), row, rows, row_index, times_ms, headers)


def _calculate(parsed, row, rows, row_index, times_ms, headers):
    tree, aliases = parsed
    hdrs = headers if headers is not None else list(row.keys())

    def resolve_token(raw_token):
        if rows is not None and times_ms is not None:
            return _evaluate_window_spec(raw_token, hdrs, rows, row_index, times_ms)
        base, op, _ = parse_token_spec(raw_token)
        col = resolve_column_name(base, hdrs)
        if op in ('slope', 'rate', 'derivative', 'deriv', 'roc', 'delta', 'diff', 'integral', 'integ'):
            return 0.0
        return finite(row[col])

    def visit(node):
        if isinstance(node, ast.Expression):
            return visit(node.body)
        if isinstance(node, ast.Constant) and type(node.value) in (int, float):
            return finite(node.value)
        if isinstance(node, ast.Name) and node.id in aliases:
            return resolve_token(aliases[node.id])
        if isinstance(node, ast.UnaryOp):
            val = visit(node.operand)
            if isinstance(node.op, ast.USub):
                return finite(-val)
            if isinstance(node.op, ast.UAdd):
                return finite(val)
            if isinstance(node.op, ast.Not):
                return 1.0 if val == 0 else 0.0
        if isinstance(node, ast.BinOp):
            a, b = visit(node.left), visit(node.right)
            if isinstance(node.op, ast.Add):
                return finite(a + b)
            if isinstance(node.op, ast.Sub):
                return finite(a - b)
            if isinstance(node.op, ast.Mult):
                return finite(a * b)
            if isinstance(node.op, ast.Div):
                return finite(a / b)
            if isinstance(node.op, ast.Mod):
                return finite(a % b)
        if isinstance(node, ast.BoolOp):
            vals = [visit(v) for v in node.values]
            if isinstance(node.op, ast.And):
                return 1.0 if all(v != 0 for v in vals) else 0.0
            if isinstance(node.op, ast.Or):
                return 1.0 if any(v != 0 for v in vals) else 0.0
        if isinstance(node, ast.Compare):
            left = visit(node.left)
            for op, comp in zip(node.ops, node.comparators):
                right = visit(comp)
                ok = (
                    abs(left - right) < 1e-9 if isinstance(op, ast.Eq) else
                    abs(left - right) >= 1e-9 if isinstance(op, ast.NotEq) else
                    left < right if isinstance(op, ast.Lt) else
                    left <= right if isinstance(op, ast.LtE) else
                    left > right if isinstance(op, ast.Gt) else
                    left >= right
                )
                if not ok:
                    return 0.0
                left = right
            return 1.0
        if isinstance(node, ast.Call):
            fn_name = node.func.id.lower()
            _, fn = SAFE_FUNCTIONS[fn_name]
            args = [visit(a) for a in node.args]
            return finite(fn(*args))
        raise ValueError('Use numbers, [column names], +, -, *, /, comparisons, and supported VCM math functions.')

    return finite(visit(tree))


def load_vcm_math_xml(path):
    data = Path(path).read_bytes()
    if len(data) > 256 * 1024 or b'<!DOCTYPE' in data.upper() or b'<!ENTITY' in data.upper():
        raise ValueError('VCM MathParameter XML must be UTF-8 XML without DTDs, at most 256 KiB.')
    root = ET.fromstring(data.decode('utf-8-sig'))
    items = [root] if root.tag == 'MathParameter' else root.findall('.//MathParameter')
    if not items:
        raise ValueError('No <MathParameter> found in XML.')
    results = []
    for el in items:
        name = (el.get('Name') or el.get('name') or Path(path).stem.replace('.MathParameter', '')).strip()
        conv = el.find('Conversion')
        expr = (el.get('Expression') or el.get('expression') or (conv.get('expression') if conv is not None else '') or (el.findtext('Expression') or '')).strip()
        expr = expr.replace('&lt;', '<').replace('&gt;', '>').replace('&amp;', '&')
        units = (el.get('Units') or el.get('units') or (conv.get('units') if conv is not None else '') or '').strip()
        if name and expr:
            results.append({'name': name, 'expression': expr, 'units': units})
    if not results:
        raise ValueError('MathParameter XML is missing Name or Expression.')
    first = dict(results[0])
    first['items'] = results
    return first


def load_csv(path):
    with open(path, encoding='utf-8-sig', newline='') as stream:
        reader = csv.DictReader(stream)
        if not reader.fieldnames:
            raise ValueError('CSV has no header.')
        if len(set(reader.fieldnames)) != len(reader.fieldnames):
            raise ValueError('CSV contains duplicate column headers.')
        rows = list(reader)
        if not rows:
            raise ValueError('CSV contains no data rows.')
        return list(reader.fieldnames), rows


def _time_order(rows):
    times = [_extract_row_time_ms(row, index) for index, row in enumerate(rows)]
    indexes = sorted(range(len(rows)), key=times.__getitem__)
    ranks = [0] * len(rows)
    for rank, index in enumerate(indexes):
        ranks[index] = rank
    return [times[index] for index in indexes], [rows[index] for index in indexes], ranks


def add_math(headers, rows, name, expression):
    if not name.strip() or name in headers:
        raise ValueError('Choose a new column name.')
    result, missing = [], 0
    raw_tokens = re.findall(r'\[([^\]]+)\]', expression)
    for token in raw_tokens:
        base, _, _ = parse_token_spec(token)
        col = resolve_column_name(base, headers)
        if col not in headers:
            raise ValueError('Missing source column: ' + base)
    parsed = parse_math(expression)
    times_ms, sorted_rows, ranks = _time_order(rows)
    for idx, row in enumerate(rows):
        new = dict(row)
        try:
            new[name] = format(_calculate(parsed, row, sorted_rows, ranks[idx], times_ms, headers), '.10g')
        except (ValueError, ZeroDivisionError, TypeError, KeyError):
            new[name] = ''
            missing += 1
        result.append(new)
    return headers + [name], result, missing


def histogram(rows, x, y, value, x_step, y_step, filter_expression=None):
    x_step, y_step = finite(x_step), finite(y_step)
    if x_step <= 0 or y_step <= 0:
        raise ValueError('Bin widths must be positive.')
    buckets = {}
    headers = list(rows[0].keys()) if rows else []
    if filter_expression:
        parsed_filter = parse_math(filter_expression)
        times_ms, sorted_rows, ranks = _time_order(rows)
    for idx, row in enumerate(rows):
        try:
            if filter_expression:
                cond = _calculate(parsed_filter, row, sorted_rows, ranks[idx], times_ms, headers)
                if abs(cond) < 1e-9:
                    continue
            a, b, v = finite(row[x]), finite(row[y]), finite(row[value])
            key = math.floor(a / x_step) * x_step, math.floor(b / y_step) * y_step
            buckets.setdefault(key, []).append(v)
        except (ValueError, KeyError, TypeError, ZeroDivisionError):
            continue
    return [
        {'x': key[0], 'y': key[1], 'count': len(vals), 'mean': sum(vals) / len(vals), 'minimum': min(vals), 'maximum': max(vals)}
        for key, vals in sorted(buckets.items())
    ]
