"""Native Linux desktop: calibration editor, PCM workflow and CSV analysis."""
import argparse
import csv
import io
import json
import queue
import re
from dataclasses import asdict
from pathlib import Path
import tkinter as tk
from tkinter import ttk, filedialog, messagebox, simpledialog
from . import __version__, core, logs
from .backend import BackendRunner, ROOT

SURFACE = '#12161e'
PANEL = '#181d26'
HEADER = '#1c2431'
EDGE = '#2c3545'
INK = '#e0e8f4'
MUTED = '#a9b7cd'
ACCENT = '#639dff'
SELECTED = '#254571'


class App(tk.Tk):
    def __init__(self, pcm_only=False, port=None):
        super().__init__()
        self.title('WrongWay Suite • PCM & flashing' if pcm_only else 'WrongWay Tuner • Linux ' + __version__)
        self.geometry('1280x850')
        self.minsize(1200, 760)
        self.configure(bg=SURFACE)
        self.option_add('*Background', SURFACE)
        self.option_add('*Foreground', INK)
        self.option_add('*insertBackground', INK)
        self.image, self.path, self.current = None, None, None
        self.saved = b''
        self.params = []
        self.definition_title = 'Custom definitions'
        self.expected_os = tk.StringVar()
        self.definition_checked = tk.BooleanVar()
        self.defs_dirty = False
        self.runner = BackendRunner()
        self.csv_headers, self.csv_rows = [], []
        self.last_read, self.last_log = None, None
        self.status = tk.StringVar(value='Ready • Select your adapter port' if pcm_only else 'Ready • Open a BIN or connect your adapter')
        style = ttk.Style(self)
        style.theme_use('clam')
        style.configure('.', font=('DejaVu Sans', 10), background=PANEL, foreground=INK,
                        bordercolor=EDGE, lightcolor=EDGE, darkcolor=EDGE, troughcolor=SURFACE,
                        fieldbackground=SURFACE, selectbackground=SELECTED, selectforeground=INK,
                        insertcolor=INK, arrowcolor=INK)
        style.configure('TButton', padding=(9, 7), background=HEADER)
        style.map('TButton', background=[('pressed', SELECTED), ('active', EDGE)],
                  foreground=[('disabled', MUTED)])
        style.configure('TNotebook.Tab', padding=(20, 10), font=('DejaVu Sans', 11, 'bold'))
        style.map('TNotebook.Tab', background=[('selected', SELECTED), ('active', HEADER)])
        style.map('TCombobox', fieldbackground=[('readonly', SURFACE)],
                  foreground=[('readonly', INK)], selectbackground=[('readonly', SELECTED)])
        style.configure('Treeview', rowheight=27, background=SURFACE, fieldbackground=SURFACE)
        style.map('Treeview', background=[('selected', SELECTED)], foreground=[('selected', INK)])
        style.configure('Treeview.Heading', font=('DejaVu Sans', 10, 'bold'), padding=6, background=HEADER)
        style.configure('TProgressbar', background=ACCENT)
        self.option_add('*TCombobox*Listbox.background', SURFACE)
        self.option_add('*TCombobox*Listbox.foreground', INK)
        self.option_add('*TCombobox*Listbox.selectBackground', SELECTED)
        top = tk.Frame(self, bg=SURFACE, padx=24, pady=20)
        top.pack(fill='x')
        heading = 'PCM  HAMMER' if pcm_only else 'WRONGWAY  TUNER'
        subtitle = 'Read, verify and flash' if pcm_only else 'Custom parameters included'
        tk.Label(top, text=heading, fg=INK, bg=SURFACE, font=('DejaVu Sans', 21, 'bold')).pack(side='left')
        tk.Label(top, text='LINUX  /  P01 + P59\n' + subtitle, justify='right', fg=ACCENT, bg=SURFACE).pack(side='right')
        self.book = ttk.Notebook(self)
        self.book.pack(fill='both', expand=True, padx=12, pady=(0, 8))
        self.editor, self.pcm, self.logtab, self.help = [ttk.Frame(self.book, padding=12) for _ in range(4)]
        tabs = [(self.pcm, 'PCM & flashing')] if pcm_only else [
            (self.editor, 'Tune editor'), (self.pcm, 'PCM & flashing'),
            (self.logtab, 'Logs & math'), (self.help, 'Getting started'),
        ]
        for frame, name in tabs:
            self.book.add(frame, text=name)
        if not pcm_only:
            self.make_editor()
        self.make_pcm()
        if port:
            self.port.set(port)
        if not pcm_only:
            self.make_logs()
            self.make_help()
            for key, callback in [('o', self.open_bin), ('s', self.save_bin), ('z', self.undo), ('y', self.redo)]:
                self.bind('<Control-' + key + '>', lambda e, action=callback: self.guard(action))
        tk.Label(self, textvariable=self.status, bg=SURFACE, fg=MUTED, anchor='w', padx=18, pady=9).pack(side='bottom', before=self.book, fill='x')
        self.protocol('WM_DELETE_WINDOW', self.close)
        self.after(100, self.poll)

    def guard(self, callback, *args):
        try:
            return callback(*args)
        except Exception as exc:
            messagebox.showerror('WrongWay Tuner', str(exc), parent=self)

    def button(self, parent, text, callback):
        b = ttk.Button(parent, text=text, command=lambda: self.guard(callback))
        b.pack(side='left', padx=(0, 6), pady=3)
        return b

    def bar(self, parent, actions):
        frame = ttk.Frame(parent)
        frame.pack(fill='x', pady=4)
        for label, action in actions:
            self.button(frame, label, action)
        return frame

    def tree(self, parent, columns, height=12):
        frame = ttk.Frame(parent)
        frame.pack(fill='both', expand=True)
        tree = ttk.Treeview(frame, columns=columns, show='headings', height=height)
        v = ttk.Scrollbar(frame, orient='vertical', command=tree.yview)
        h = ttk.Scrollbar(frame, orient='horizontal', command=tree.xview)
        tree.configure(yscrollcommand=v.set, xscrollcommand=h.set)
        tree.grid(row=0, column=0, sticky='nsew')
        v.grid(row=0, column=1, sticky='ns')
        h.grid(row=1, column=0, sticky='ew')
        frame.rowconfigure(0, weight=1)
        frame.columnconfigure(0, weight=1)
        self.configure_columns(tree, columns)
        return tree

    def configure_columns(self, tree, columns):
        tree.configure(columns=columns)
        for col in columns:
            tree.heading(col, text=col)
            tree.column(col, width=125, minwidth=70, anchor='e', stretch=False)

    def make_editor(self):
        self.bar(self.editor, [('Open BIN', self.open_bin), ('Save BIN as', self.save_bin), ('Load XDF / JSON', self.load_defs), ('Save definitions', self.save_defs), ('Undo', self.undo), ('Redo', self.redo)])
        line = ttk.Frame(self.editor)
        line.pack(fill='x', pady=(5, 10))
        self.file_info = tk.StringVar(value='No BIN loaded')
        ttk.Label(line, textvariable=self.file_info).pack(side='left')
        ttk.Label(line, text='Expected OS:').pack(side='left', padx=(15, 5))
        ttk.Entry(line, textvariable=self.expected_os, width=12).pack(side='left')
        ttk.Checkbutton(line, text='Definition matches this OS', variable=self.definition_checked).pack(side='left', padx=8)
        panes = ttk.Panedwindow(self.editor, orient='horizontal')
        panes.pack(fill='both', expand=True)
        side, content = ttk.Frame(panes, padding=(0, 0, 10, 0)), ttk.Frame(panes)
        panes.add(side, weight=1)
        panes.add(content, weight=4)
        self.search = tk.StringVar()
        ttk.Entry(side, textvariable=self.search).pack(fill='x', pady=(0, 8))
        self.search.trace_add('write', lambda *_: self.refresh_parameters())
        self.parameter_tree = ttk.Treeview(side, show='tree', selectmode='browse')
        self.parameter_tree.pack(fill='both', expand=True)
        self.parameter_tree.bind('<<TreeviewSelect>>', self.select_parameter)
        self.bar(side, [('Add', self.add_parameter), ('Edit', self.edit_parameter), ('Remove', self.remove_parameter)])
        self.param_info = tk.StringVar(value='Load definitions to view scalars, switches and tables.')
        ttk.Label(content, textvariable=self.param_info, wraplength=830, font=('DejaVu Sans', 11, 'bold')).pack(fill='x', pady=(0, 10))
        self.table = self.tree(content, ['Value'])
        self.table.tag_configure('changed', background='#463628')
        self.table.bind('<Double-1>', self.edit_cell)
        self.bar(content, [('Copy table', self.copy_table), ('Paste table', self.paste_table), ('CSV export', self.export_table), ('CSV import', self.import_table), ('Checksums', self.checksums), ('Compare BIN', self.compare)])
        batch = ttk.Frame(content)
        batch.pack(fill='x', pady=6)
        self.row_range, self.col_range = tk.StringVar(value='all'), tk.StringVar(value='all')
        self.operation, self.amount = tk.StringVar(value='Multiply'), tk.StringVar(value='1.0')
        for label, var in [('Rows', self.row_range), ('Columns', self.col_range)]:
            ttk.Label(batch, text=label).pack(side='left', padx=(0, 4))
            ttk.Entry(batch, textvariable=var, width=8).pack(side='left', padx=(0, 8))
        ttk.Combobox(batch, textvariable=self.operation, values=['Set', 'Add', 'Multiply', 'Percent', 'Smooth'], state='readonly', width=11).pack(side='left', padx=(0, 6))
        ttk.Entry(batch, textvariable=self.amount, width=9).pack(side='left', padx=(0, 6))
        self.button(batch, 'Apply to range', self.batch_edit)
        ttk.Label(content, text='Double-click to edit. Ranges use zero-based indices, e.g. 0:3. Changed rows are yellow.').pack(anchor='w')

    def discard(self):
        return not self.image or bytes(self.image.data) == self.saved or messagebox.askyesno('Unsaved BIN', 'Discard unsaved BIN changes?', parent=self)

    def discard_defs(self):
        return not self.defs_dirty or messagebox.askyesno('Unsaved definitions', 'Discard unsaved custom definitions?', parent=self)

    def open_bin(self, path=None):
        if not self.discard() or not self.discard_defs():
            return
        path = path or filedialog.askopenfilename(title='Open raw PCM BIN', filetypes=[('Raw BIN', '*.bin'), ('All files', '*')])
        if not path:
            return
        path = Path(path).absolute()
        if path.stat().st_size > 16 * 1024 * 1024:
            raise ValueError('BIN exceeds the 16 MiB editor limit.')
        candidate = core.TuneImage(path.read_bytes())
        self.path, self.image, self.saved = path, candidate, bytes(candidate.data)
        self.params, self.current, self.defs_dirty = [], None, False
        self.definition_checked.set(False)
        self.expected_os.set('')
        self.refresh_parameters()
        self.refresh_table()
        self.book.select(self.editor)
        self.update_info()

    def require_image(self):
        if not self.image:
            raise ValueError('Open a BIN first.')

    def update_info(self):
        if not self.image:
            return
        label = f'{self.path.name} • {len(self.image.data):,} bytes • {self.image.changed_bytes:,} changed'
        try:
            controller, osid, _ = core.pcm_layout(self.image.data)
            label += f' • {controller} / OS {osid}'
        except ValueError:
            pass
        self.file_info.set(label)
        self.status.set(label)

    def allow_edit(self):
        self.require_image()
        if not self.definition_checked.get():
            raise ValueError('Check the definition match before editing.')
        expected = self.expected_os.get().strip()
        if expected:
            _, actual, _ = core.pcm_layout(self.image.data)
            if expected != str(actual):
                raise ValueError(f'Expected OS {expected}; BIN contains OS {actual}.')

    def save_bin(self):
        self.require_image()
        path = filedialog.asksaveasfilename(initialfile=self.path.stem + '-edited.bin', defaultextension='.bin')
        if path:
            backup = core.atomic_save(path, self.image.data)
            self.saved = bytes(self.image.data)
            self.status.set('Saved ' + path + (' • Backup: ' + backup.name if backup else ''))

    def load_defs(self, path=None):
        self.require_image()
        if not self.discard_defs():
            return
        path = path or filedialog.askopenfilename(filetypes=[('Definitions', '*.xdf *.json'), ('All files', '*')])
        if not path:
            return
        if Path(path).suffix.lower() == '.xdf':
            title, params, warnings = core.import_xdf(path, len(self.image.data))
            expected = ''
        else:
            document, params = core.load_definitions(path, len(self.image.data))
            title, expected, warnings = document.get('Title', 'Custom'), document.get('ExpectedOS', ''), []
        self.definition_title, self.params, self.current = title, params, None
        self.expected_os.set(expected)
        self.definition_checked.set(False)
        self.defs_dirty = False
        self.refresh_parameters()
        self.refresh_table()
        self.status.set(f'{len(params)} parameters loaded • {len(warnings)} import notices')
        if warnings:
            self.text_window('XDF import notices', '\n'.join(warnings))

    def save_defs(self):
        if not self.params:
            raise ValueError('Add or import definitions first.')
        path = filedialog.asksaveasfilename(initialfile='custom.parameters.json', defaultextension='.json')
        if path:
            core.save_definitions(path, self.definition_title, self.expected_os.get(), self.params)
            self.defs_dirty = False
            self.status.set('Definitions saved: ' + path)

    def refresh_parameters(self):
        self.parameter_tree.delete(*self.parameter_tree.get_children())
        groups, text = {}, self.search.get().casefold()
        for i, p in enumerate(self.params):
            if text not in (p.Title + ' ' + p.Group).casefold():
                continue
            if p.Group not in groups:
                groups[p.Group] = self.parameter_tree.insert('', 'end', text=p.Group, open=True)
            self.parameter_tree.insert(groups[p.Group], 'end', iid='p' + str(i), text=p.Title)

    def select_parameter(self, event=None):
        selected = self.parameter_tree.selection()
        if selected and selected[0].startswith('p'):
            self.current = self.params[int(selected[0][1:])]
            self.guard(self.refresh_table)

    def refresh_table(self):
        self.table.delete(*self.table.get_children())
        if not self.current or not self.image:
            self.param_info.set('Load definitions to view scalars, switches and tables.')
            return
        p = self.current
        p.validate(len(self.image.data))
        self.configure_columns(self.table, ['row'] + [str(c) for c in range(p.Columns)])
        self.table.heading('row', text='Row')
        self.table.column('row', width=100, anchor='w')
        for c in range(p.Columns):
            self.table.heading(str(c), text=p.ColumnLabels[c] if p.ColumnLabels and c < len(p.ColumnLabels) else str(c))
        for row in range(p.Rows):
            label = p.RowLabels[row] if p.RowLabels and row < len(p.RowLabels) else str(row)
            values = [label] + [format(self.image.read(p, row, col), '.9g') for col in range(p.Columns)]
            changed = any(self.image.cell_changed(p, row, col) for col in range(p.Columns))
            self.table.insert('', 'end', iid=str(row), values=values, tags=('changed',) if changed else ())
        self.param_info.set(f'{p.Title} • {p.Units or p.Storage} • {p.Address} • {p.Rows} × {p.Columns}\n{p.Notes[:160]}')
        self.update_info()

    def edit_cell(self, event):
        def perform():
            self.allow_edit()
            rowid, column = self.table.identify_row(event.y), self.table.identify_column(event.x)
            if not rowid or not column or not self.current:
                return
            col = int(column[1:]) - 2
            if col < 0:
                return
            row = int(rowid)
            value = simpledialog.askfloat(self.current.Title, 'New value' + (' (0 off / 1 on)' if self.current.Storage == 'Flag' else '') + ':', initialvalue=self.image.read(self.current, row, col), parent=self)
            if value is not None:
                self.image.write(self.current, row, col, value)
                self.refresh_table()
        self.guard(perform)

    def parameter_dialog(self, p, callback):
        window = tk.Toplevel(self)
        window.title('Custom parameter • ' + p.Title)
        window.geometry('730x680')
        ttk.Label(window, text='Address is a raw BIN offset. Displayed value = raw × Scale + Offset.', padding=12).pack(fill='x')
        text = tk.Text(window, font=('DejaVu Sans Mono', 10), wrap='none', padx=12, pady=12)
        text.pack(fill='both', expand=True, padx=12)
        text.insert('1.0', json.dumps(asdict(p), indent=2))
        def apply():
            candidate = core.Parameter.from_dict(json.loads(text.get('1.0', 'end')))
            candidate.validate(len(self.image.data))
            callback(candidate)
            self.defs_dirty, self.current = True, candidate
            self.definition_checked.set(False)
            self.refresh_parameters()
            self.refresh_table()
            window.destroy()
        ttk.Button(window, text='Apply definition', command=lambda: self.guard(apply)).pack(pady=12)

    def add_parameter(self):
        self.require_image()
        self.parameter_dialog(core.Parameter(), lambda p: self.params.append(p))

    def edit_parameter(self):
        self.require_parameter()
        index = self.params.index(self.current)
        self.parameter_dialog(self.current, lambda p: self.params.__setitem__(index, p))

    def remove_parameter(self):
        if self.current and messagebox.askyesno('Remove parameter', 'Remove ' + self.current.Title + '?', parent=self):
            self.params.remove(self.current)
            self.current, self.defs_dirty = None, True
            self.refresh_parameters()
            self.refresh_table()

    def undo(self):
        self.require_image()
        self.image.undo()
        self.refresh_table()
        self.update_info()

    def redo(self):
        self.require_image()
        self.image.redo()
        self.refresh_table()
        self.update_info()

    def require_parameter(self):
        self.require_image()
        if not self.current:
            raise ValueError('Select a parameter first.')

    def copy_table(self):
        self.require_parameter()
        self.clipboard_clear()
        self.clipboard_append(core.export_table(self.image, self.current))

    def paste_table(self):
        self.allow_edit()
        self.require_parameter()
        self.image.batch(self.current, core.parse_table(self.clipboard_get(), self.current))
        self.refresh_table()

    def export_table(self):
        self.require_parameter()
        path = filedialog.asksaveasfilename(initialfile='table.csv', defaultextension='.csv')
        if path:
            core.atomic_save(path, core.export_table(self.image, self.current).encode())

    def import_table(self):
        self.allow_edit()
        self.require_parameter()
        path = filedialog.askopenfilename(filetypes=[('CSV / TSV', '*.csv *.tsv')])
        if path:
            self.image.batch(self.current, core.parse_table(Path(path).read_text(), self.current))
            self.refresh_table()

    def range_values(self, text, count):
        if text.strip().lower() == 'all':
            return range(count)
        parts = text.split(':')
        start, stop = int(parts[0]), int(parts[-1])
        if len(parts) > 2 or not 0 <= start <= stop < count:
            raise ValueError('Use all, a single index, or an inclusive range such as 0:3.')
        return range(start, stop + 1)

    def batch_edit(self):
        self.allow_edit()
        self.require_parameter()
        p, mode = self.current, self.operation.get()
        rows, cols = self.range_values(self.row_range.get(), p.Rows), self.range_values(self.col_range.get(), p.Columns)
        amount = core.finite(self.amount.get()) if mode != 'Smooth' else 0
        edits = []
        for row in rows:
            for col in cols:
                old = self.image.read(p, row, col)
                if mode == 'Set':
                    value = amount
                elif mode == 'Add':
                    value = old + amount
                elif mode == 'Multiply':
                    value = old * amount
                elif mode == 'Percent':
                    value = old * (1 + amount / 100)
                else:
                    neighbors = [self.image.read(p, r, c) for r in range(max(0, row - 1), min(p.Rows, row + 2)) for c in range(max(0, col - 1), min(p.Columns, col + 2))]
                    value = sum(neighbors) / len(neighbors)
                edits.append((row, col, value))
        self.image.batch(p, edits)
        self.refresh_table()

    def checksums(self):
        self.require_image()
        controller, osid, report = core.checksum_report(self.image.data)
        content = f'{controller} / OS {osid}\n\n' + '\n'.join(f'{s["name"]}: {s["stored"]:04X} / needed {s["needed"]:04X} {"OK" if s["valid"] else "BAD"}' for s in report)
        if all(s['valid'] for s in report):
            messagebox.showinfo('Checksums', content, parent=self)
        elif messagebox.askyesno('Repair checksums', content + '\n\nRepair checksum bytes? This can be undone.', parent=self):
            core.repair_checksums(self.image)
            self.refresh_table()
            self.update_info()

    def compare(self):
        self.require_image()
        path = filedialog.askopenfilename(title='Compare with another BIN', filetypes=[('BIN', '*.bin')])
        if not path:
            return
        other = Path(path).read_bytes()
        if len(other) != len(self.image.data):
            raise ValueError('BIN sizes differ.')
        offsets = [i for i, (a, b) in enumerate(zip(self.image.data, other)) if a != b]
        lines = [f'{len(offsets):,} differing bytes • {Path(path).name}', 'Current / comparison:']
        lines += [f'0x{i:06X}  {self.image.data[i]:02X} / {other[i]:02X}' for i in offsets[:2000]]
        if len(offsets) > 2000:
            lines.append('Showing first 2,000 differences.')
        self.text_window('BIN comparison', '\n'.join(lines))

    def make_pcm(self):
        ttk.Label(self.pcm, text='PCM connection', font=('DejaVu Sans', 15, 'bold')).pack(anchor='w')
        ttk.Label(self.pcm, text='OBDX Pro GT USB serial • Bluetooth Classic can use an already-bound /dev/rfcomm port.').pack(anchor='w', pady=(5, 12))
        bar = ttk.Frame(self.pcm)
        bar.pack(fill='x')
        self.port, self.adapter = tk.StringVar(value='/dev/ttyUSB0'), tk.StringVar(value='OBDX')
        self.port_box = ttk.Combobox(bar, textvariable=self.port, width=25)
        self.port_box.pack(side='left', padx=(0, 8))
        ttk.Combobox(bar, textvariable=self.adapter, values=['OBDX', 'ELM', 'AVT'], state='readonly', width=8).pack(side='left', padx=(0, 8))
        for label, action in [('Refresh ports', lambda: self.run_pcm('devices', {})), ('Read properties', lambda: self.run_pcm('identify')), ('Read full backup', self.read_backup)]:
            self.button(bar, label, action)
        self.no4x, self.debug = tk.BooleanVar(), tk.BooleanVar()
        ttk.Checkbutton(bar, text='1× VPW', variable=self.no4x).pack(side='left', padx=8)
        ttk.Checkbutton(bar, text='Debug log', variable=self.debug).pack(side='left')
        self.properties = tk.StringVar(value='No live PCM information • Hardware read/write has not been bench-tested for this release.')
        ttk.Label(self.pcm, textvariable=self.properties, wraplength=1160, padding=(0, 12)).pack(fill='x')
        flash = ttk.LabelFrame(self.pcm, text='Validate & write', padding=10)
        flash.pack(fill='x', pady=(0, 10))
        self.flash_file, self.backup_file = tk.StringVar(), tk.StringVar()
        self.flash_mode = tk.StringVar(value='Calibration only')
        for row, (label, var) in enumerate([('Edited BIN', self.flash_file), ('Original backup', self.backup_file)]):
            ttk.Label(flash, text=label).grid(row=row, column=0, sticky='w', padx=(0, 10), pady=5)
            ttk.Entry(flash, textvariable=var).grid(row=row, column=1, sticky='ew', pady=5)
            ttk.Button(flash, text='Choose', command=lambda v=var: self.choose(v, [('PCM BIN', '*.bin')])).grid(row=row, column=2, padx=8)
        flash.columnconfigure(1, weight=1)
        buttons = ttk.Frame(flash)
        buttons.grid(row=2, column=0, columnspan=3, sticky='ew', pady=4)
        for label, op in [('Validate BIN', 'validate'), ('Compare with PCM', 'verify'), ('Test write', 'test-write')]:
            self.button(buttons, label, lambda operation=op: self.run_pcm(operation, self.file_options()))
        ttk.Combobox(buttons, textvariable=self.flash_mode, state='readonly', values=['Calibration only', 'Parameters only', 'OS + calibration + boot', 'Full clone', 'P59 recovery'], width=26).pack(side='left', padx=8)
        self.button(buttons, 'Review & write', self.flash)
        other = self.bar(self.pcm, [('Read trouble codes', lambda: self.run_pcm('codes')), ('Clear trouble codes', self.clear_codes), ('Export session log', self.export_session)])
        self.stop_button = self.button(other, 'Stop / disconnect', self.runner.cancel)
        self.progress = ttk.Progressbar(other, mode='determinate', maximum=100)
        self.progress.pack(side='right', fill='x', expand=True, padx=10)
        self.session = tk.Text(self.pcm, height=14, wrap='word', bg=SURFACE, fg=INK, font=('DejaVu Sans Mono', 10), padx=12, pady=10, state='disabled')
        self.session.pack(fill='both', expand=True, pady=(10, 0))

    def choose(self, var, types):
        path = filedialog.askopenfilename(filetypes=types)
        if path:
            var.set(str(Path(path).absolute()))

    def run_pcm(self, operation, options=None):
        if options is None:
            options = {'port': self.port.get(), 'adapter': self.adapter.get(), 'no4x': self.no4x.get(), 'debug': self.debug.get()}
        self.runner.start(operation, dict(options))
        self.stop_button.configure(state='disabled' if self.runner.flashing else 'normal')
        self.status.set('Running: ' + operation)
        self.progress['value'] = 0
        self.append_session('Starting ' + operation)

    def read_backup(self):
        path = filedialog.asksaveasfilename(title='Save original full PCM backup', initialfile='stock-backup.bin', defaultextension='.bin')
        if path:
            if Path(path).exists():
                raise ValueError('Choose a new filename to preserve the existing backup.')
            self.last_read = str(Path(path).absolute())
            self.run_pcm('read', {'port': self.port.get(), 'adapter': self.adapter.get(), 'output': self.last_read, 'no4x': self.no4x.get(), 'debug': self.debug.get()})

    def file_options(self):
        path = self.flash_file.get().strip()
        if not path or not Path(path).is_file():
            raise ValueError('Choose the saved edited BIN.')
        return {'port': self.port.get(), 'adapter': self.adapter.get(), 'file': str(Path(path).absolute()), 'no4x': self.no4x.get(), 'debug': self.debug.get()}

    def flash(self):
        options = self.file_options()
        backup = self.backup_file.get().strip()
        if not backup or not Path(backup).is_file():
            raise ValueError('Choose the separate original backup before writing.')
        options.update({'backup': str(Path(backup).absolute()), 'confirm': True})
        mode = self.flash_mode.get()
        op = {'Calibration only': 'write-calibration', 'Parameters only': 'write-parameters', 'OS + calibration + boot': 'write-os', 'Full clone': 'write-full', 'P59 recovery': 'recover-p59'}[mode]
        controller, osid, report = core.checksum_report(Path(options['file']).read_bytes())
        if not all(s['valid'] for s in report):
            raise ValueError('Repair and save BIN checksums before writing.')
        text = f'{mode}\n{controller} / File OS {osid}\n{self.adapter.get()} at {self.port.get()}\n\nTune: {options["file"]}\nBackup: {options["backup"]}\n\nUse stable battery support, ignition ON and engine OFF. Keep the cable connected until completion. This Linux release has not been verified on a real PCM.\n\nBegin this write?'
        if messagebox.askyesno('Review PCM write', text, parent=self):
            self.run_pcm(op, options)

    def clear_codes(self):
        if messagebox.askyesno('Clear trouble codes', 'Send the PCM clear request? Freeze-frame and emissions readiness may reset.', parent=self):
            self.run_pcm('clear-codes', {'port': self.port.get(), 'adapter': self.adapter.get(), 'confirm': True, 'debug': self.debug.get()})

    def append_session(self, text):
        self.session.configure(state='normal')
        self.session.insert('end', text + '\n')
        count = int(self.session.index('end-1c').split('.')[0])
        if count > 5000:
            self.session.delete('1.0', str(count - 5000) + '.0')
        self.session.see('end')
        self.session.configure(state='disabled')

    def export_session(self):
        path = filedialog.asksaveasfilename(initialfile='wrongway-session.txt', defaultextension='.txt')
        if path:
            core.atomic_save(path, self.session.get('1.0', 'end').encode())

    def poll(self):
        try:
            for _ in range(100):
                self.guard(self.handle_event, self.runner.events.get_nowait())
        except queue.Empty:
            pass
        self.after(100, self.poll)

    def handle_event(self, event):
        kind = event.get('type')
        if kind == 'devices':
            self.port_box['values'] = event['ports']
            self.append_session('Serial ports: ' + (', '.join(event['ports']) or 'none; check USB connection and dialout group'))
        elif kind == 'properties':
            self.properties.set(' • '.join(key.upper() + ': ' + str(value) for key, value in event.items() if key != 'type'))
            self.append_session(self.properties.get())
        elif kind == 'prompt':
            accepted = messagebox.askyesno(event.get('title', 'PCM review'), event.get('message', ''), parent=self)
            self.runner.send('yes' if accepted else 'no')
        elif kind == 'percent':
            match = re.search(r'([0-9]+(?:\.[0-9]+)?)', event.get('message', ''))
            if match:
                self.progress['value'] = float(match.group(1))
        elif kind == 'log_header':
            self.configure_columns(self.live_tree, event['columns'])
        elif kind == 'log_row':
            children = self.live_tree.get_children()
            if len(children) >= 150:
                self.live_tree.delete(children[0])
            self.live_tree.insert('', 'end', values=event['values'])
            self.live_tree.yview_moveto(1)
        elif kind == 'codes':
            self.append_session('DTCs: ' + (', '.join(event['codes']) or ('none reported' if event['responded'] else 'no PCM response')))
        elif kind == 'result':
            self.runner.last_result = event
            self.append_session('Result: ' + json.dumps(event, ensure_ascii=False))
        elif kind == 'exit':
            self.stop_button.configure(state='normal')
            success = event['code'] == 0 and self.runner.last_result and self.runner.last_result.get('success')
            self.status.set(('Completed: ' if success else 'Ended: ') + event['operation'] + f' • exit {event["code"]}')
            self.append_session(self.status.get())
            if success and event['operation'] == 'read' and self.last_read:
                self.backup_file.set(self.last_read)
            if success and event['operation'] == 'log' and self.last_log:
                self.open_log(self.last_log)
        elif kind == 'activity':
            self.status.set(event.get('message', ''))
        elif kind not in ('remaining', 'speed', 'retries'):
            self.append_session(event.get('message', json.dumps(event)))

    def make_logs(self):
        self.bar(self.logtab, [('Open CSV', self.open_log), ('Save CSV', self.save_log), ('Add calculated channel', self.math_channel), ('RPM / MAP histogram', self.make_histogram)])
        self.profile = tk.StringVar(value=str(ROOT / 'backend/WrongWayP59.LogProfile'))
        live = ttk.Frame(self.logtab)
        live.pack(fill='x', pady=8)
        ttk.Label(live, text='Live profile').pack(side='left', padx=(0, 8))
        ttk.Entry(live, textvariable=self.profile, width=55).pack(side='left', fill='x', expand=True, padx=(0, 8))
        self.button(live, 'Choose profile', lambda: self.choose(self.profile, [('Profiles', '*.LogProfile *.xml')]))
        self.button(live, 'Start live log', self.start_log)
        self.button(live, 'Stop log', self.runner.cancel)
        ttk.Label(self.logtab, text='AFR preset: (([7101.10]/.5)+9.57) • Source must be logged A/C sensor voltage.').pack(anchor='w', pady=(0, 8))
        self.live_tree = self.tree(self.logtab, ['Time', 'Values'], height=8)
        chartbar = ttk.Frame(self.logtab)
        chartbar.pack(fill='x', pady=8)
        self.chart_column = tk.StringVar()
        self.chart_box = ttk.Combobox(chartbar, textvariable=self.chart_column, state='readonly', width=45)
        self.chart_box.pack(side='left', padx=(0, 8))
        self.button(chartbar, 'Plot channel', self.plot)
        self.log_info = tk.StringVar(value='Open CSV or start a live session; live rows save directly to disk.')
        ttk.Label(chartbar, textvariable=self.log_info).pack(side='left', padx=10)
        self.canvas = tk.Canvas(self.logtab, height=230, bg='#15253b', highlightthickness=0)
        self.canvas.pack(fill='both', expand=True)
        self.canvas.bind('<Configure>', lambda e: self.guard(self.plot) if self.csv_rows else None)

    def open_log(self, path=None):
        path = path or filedialog.askopenfilename(filetypes=[('CSV logs', '*.csv')])
        if not path:
            return
        self.csv_headers, self.csv_rows = logs.load_csv(path)
        self.chart_box['values'] = self.csv_headers
        self.chart_column.set(self.csv_headers[min(1, len(self.csv_headers) - 1)])
        self.populate_log()
        self.log_info.set(f'{len(self.csv_rows):,} samples • {Path(path).name}')
        self.plot()

    def populate_log(self):
        self.configure_columns(self.live_tree, self.csv_headers)
        self.live_tree.delete(*self.live_tree.get_children())
        for row in self.csv_rows[:150]:
            self.live_tree.insert('', 'end', values=[row.get(col, '') for col in self.csv_headers])

    def start_log(self):
        path = filedialog.asksaveasfilename(title='Save live log', initialfile='pcm-live.csv', defaultextension='.csv')
        if path:
            if Path(path).exists():
                raise ValueError('Choose a new live log filename.')
            self.last_log = str(Path(path).absolute())
            self.live_tree.delete(*self.live_tree.get_children())
            self.run_pcm('log', {'port': self.port.get(), 'adapter': self.adapter.get(), 'profile': self.profile.get(), 'output': self.last_log, 'debug': self.debug.get()})

    def save_log(self):
        if not self.csv_rows:
            raise ValueError('Load a CSV first.')
        path = filedialog.asksaveasfilename(initialfile='calculated-log.csv', defaultextension='.csv')
        if path:
            stream = io.StringIO()
            writer = csv.DictWriter(stream, fieldnames=self.csv_headers)
            writer.writeheader()
            writer.writerows(self.csv_rows)
            core.atomic_save(path, stream.getvalue().encode())

    def math_channel(self):
        if not self.csv_rows:
            raise ValueError('Load a CSV first.')
        window = tk.Toplevel(self)
        window.title('Calculated scanner channel')
        window.geometry('750x260')
        name, formula = tk.StringVar(value='AFR'), tk.StringVar(value=logs.AFR_EXPRESSION)
        for title, var in [('Column name', name), ('Formula • surround CSV columns with [brackets]', formula)]:
            ttk.Label(window, text=title, padding=10).pack(anchor='w')
            ttk.Entry(window, textvariable=var).pack(fill='x', padx=10)
        ttk.Label(window, text='Available: ' + ', '.join(self.csv_headers), wraplength=720, padding=10).pack(anchor='w')
        def apply():
            self.csv_headers, self.csv_rows, missing = logs.add_math(self.csv_headers, self.csv_rows, name.get(), formula.get())
            self.chart_box['values'] = self.csv_headers
            self.chart_column.set(name.get())
            self.populate_log()
            self.plot()
            self.status.set(f'{name.get()} calculated • {missing} missing/invalid rows')
            window.destroy()
        ttk.Button(window, text='Calculate', command=lambda: self.guard(apply)).pack(pady=10)

    def plot(self):
        self.canvas.delete('all')
        if not self.csv_rows or not self.chart_column.get():
            return
        points = []
        for i, row in enumerate(self.csv_rows):
            try:
                points.append((i, core.finite(row[self.chart_column.get()])))
            except (ValueError, TypeError, KeyError):
                continue
        if not points:
            self.canvas.create_text(30, 25, text='No numeric samples.', anchor='nw', fill='white')
            return
        low, high = min(y for _, y in points), max(y for _, y in points)
        if high == low:
            low, high = low - 1, high + 1
        w, h = max(300, self.canvas.winfo_width()), max(160, self.canvas.winfo_height())
        left, right, top, bottom = 80, w - 25, 30, h - 38
        for t in range(5):
            y = top + (bottom - top) * t / 4
            self.canvas.create_line(left, y, right, y, fill='#30435b')
            self.canvas.create_text(left - 10, y, text=f'{high - (high - low) * t / 4:.4g}', anchor='e', fill='#bed3e8')
        self.canvas.create_text(left, 12, text=self.chart_column.get(), anchor='w', fill='#edf6ff')
        self.canvas.create_text(left, bottom + 22, text='Sample 0', anchor='w', fill='#bed3e8')
        self.canvas.create_text(right, bottom + 22, text=f'Sample {len(self.csv_rows) - 1}', anchor='e', fill='#bed3e8')
        coords = []
        for x, y in points[::max(1, len(points) // 4000)]:
            coords.extend((left + x / max(1, len(self.csv_rows) - 1) * (right - left), bottom - (y - low) / (high - low) * (bottom - top)))
        if len(coords) >= 4:
            self.canvas.create_line(*coords, fill='#63c4ff', width=2)

    def make_histogram(self):
        if not self.csv_rows:
            raise ValueError('Load a CSV first.')
        window = tk.Toplevel(self)
        window.title('Log histogram')
        window.geometry('800x570')
        options = ttk.Frame(window, padding=12)
        options.pack(fill='x')
        variables = [tk.StringVar(value=self.csv_headers[min(i, len(self.csv_headers) - 1)]) for i in range(3)]
        steps = [tk.StringVar(value='400'), tk.StringVar(value='10')]
        for i, label in enumerate(['X (RPM)', 'Y (MAP)', 'Value']):
            ttk.Label(options, text=label).grid(row=0, column=i)
            ttk.Combobox(options, textvariable=variables[i], values=self.csv_headers, state='readonly', width=25).grid(row=1, column=i, padx=5)
        for i, label in enumerate(['X bin width', 'Y bin width']):
            ttk.Label(options, text=label).grid(row=2, column=i, pady=(10, 0))
            ttk.Entry(options, textvariable=steps[i], width=15).grid(row=3, column=i)
        columns = ['x', 'y', 'count', 'mean', 'minimum', 'maximum']
        tree = self.tree(window, columns)
        result = []
        def compute():
            result[:] = logs.histogram(self.csv_rows, *(v.get() for v in variables), *(v.get() for v in steps))
            tree.delete(*tree.get_children())
            for row in result:
                tree.insert('', 'end', values=[format(row[col], '.7g') for col in columns])
        def export():
            if not result:
                return
            path = filedialog.asksaveasfilename(defaultextension='.csv', initialfile='histogram.csv', parent=window)
            if path:
                stream = io.StringIO()
                writer = csv.DictWriter(stream, fieldnames=columns)
                writer.writeheader()
                writer.writerows(result)
                core.atomic_save(path, stream.getvalue().encode())
        self.bar(window, [('Calculate bins', compute), ('Export CSV', export)])

    def text_window(self, title, content):
        window = tk.Toplevel(self)
        window.title(title)
        window.geometry('800x550')
        text = tk.Text(window, wrap='word', font=('DejaVu Sans Mono', 10), padx=12, pady=12)
        text.pack(fill='both', expand=True)
        text.insert('1.0', content)
        text.configure(state='disabled')

    def make_help(self):
        content = (ROOT / 'QUICKSTART.txt').read_text()
        text = tk.Text(self.help, wrap='word', bg=SURFACE, fg=INK, borderwidth=0, font=('DejaVu Sans', 11), padx=8, pady=8)
        text.pack(fill='both', expand=True)
        text.insert('1.0', content)
        text.configure(state='disabled')
        self.bar(self.help, [('Open synthetic demo', self.demo), ('Check PCM backend', lambda: self.run_pcm('self-test', {}))])

    def demo(self):
        self.open_bin(ROOT / 'examples/demo.bin')
        if self.path != ROOT / 'examples/demo.bin':
            return
        self.load_defs(ROOT / 'examples/demo.parameters.json')
        self.expected_os.set('')
        self.definition_checked.set(True)
        if self.params:
            self.current = next((p for p in self.params if p.Rows > 1), self.params[0])
            self.refresh_table()

    def close(self):
        if self.runner.flashing:
            messagebox.showinfo('PCM write running', 'Wait for the write to finish before closing.', parent=self)
            return
        if self.runner.running:
            self.runner.cancel()
            self.status.set('Waiting for adapter operation to stop…')
            self.after(500, self.close)
            return
        if self.discard() and self.discard_defs():
            self.destroy()


def main():
    parser = argparse.ArgumentParser(description='WrongWay Tuner and PCM Hammer')
    parser.add_argument('--pcm-only', action='store_true', help='open the suite PCM window')
    parser.add_argument('--port', help='preselect a Linux serial device')
    options = parser.parse_args()
    try:
        app = App(pcm_only=options.pcm_only, port=options.port)
    except tk.TclError as exc:
        raise SystemExit('A Linux graphical desktop is required. ' + str(exc))
    app.mainloop()


if __name__ == '__main__':
    main()
