"""Requires a graphical display; uses only synthetic files and offline operations."""
import os
from pathlib import Path
import tempfile
import time
from unittest.mock import patch
from wrongway.app import App
from wrongway import core, logs

app = App()
app.update()
checks = 0


def check(condition, title):
    global checks
    assert condition, title
    checks += 1
    print('PASS', title)


try:
    app.demo()
    app.update()
    check(len(app.params) == 3 and len(app.table.get_children()) == 4, 'demo opens and table renders')
    app.allow_edit()
    app.operation.set('Add')
    app.amount.set('1')
    old = bytes(app.image.data)
    app.batch_edit()
    check(app.image.changed_bytes == 16, 'desktop range edit reaches all table cells')
    app.undo()
    check(bytes(app.image.data) == old, 'desktop undo restores the entire batch')
    app.redo()
    check(app.image.changed_bytes == 16, 'desktop redo reapplies batch')
    app.undo()
    app.copy_table()
    check('20,21,22,23' in app.clipboard_get(), 'clipboard copy preserves converted values')
    app.clipboard_clear()
    app.clipboard_append('1,2,3,4\n5,6,7,8\n9,10,11,12\n13,14,15,16')
    app.paste_table()
    check(app.image.read(app.current, 3, 3) == 16, 'clipboard paste updates all table values')
    app.undo()
    app.expected_os.set('12587603')
    try:
        app.allow_edit()
    except ValueError:
        checks += 1
        print('PASS incompatible OS blocks editing')
    else:
        raise AssertionError('Expected OS guard')
    app.expected_os.set('')
    with tempfile.TemporaryDirectory() as folder:
        folder = Path(folder)
        path = folder / 'saved.bin'
        with patch('wrongway.app.filedialog.asksaveasfilename', return_value=str(path)):
            app.save_bin()
        check(path.read_bytes() == app.image.data, 'desktop save writes the exact image')
        definitions = folder / 'saved.parameters.json'
        with patch('wrongway.app.filedialog.asksaveasfilename', return_value=str(definitions)):
            app.save_defs()
        check(len(core.load_definitions(definitions, 128)[1]) == 3, 'desktop saves interoperable definitions')
        logfile = folder / 'log.csv'
        logfile.write_text('Time,RPM,MAP,7101.10\n0,800,40,2.5\n1,900,45,3\n')
        app.open_log(logfile)
        app.update()
        check(len(app.live_tree.get_children()) == 2, 'CSV opens in desktop')
        app.csv_headers, app.csv_rows, _ = logs.add_math(app.csv_headers, app.csv_rows, 'AFR', logs.AFR_EXPRESSION)
        app.chart_column.set('AFR')
        app.populate_log()
        app.plot()
        check(len(app.canvas.find_all()) >= 10, 'calculated AFR channel plots')
        check(app.csv_rows[0]['AFR'] == '14.57', 'desktop AFR honors the user formula')
    app.make_histogram()
    app.update()
    check(any(c.winfo_class() == 'Toplevel' for c in app.winfo_children()), 'histogram controls open')
    for child in app.winfo_children():
        if child.winfo_class() == 'Toplevel':
            child.destroy()
    app.run_pcm('self-test', {})
    deadline = time.monotonic() + 10
    while (app.runner.running or not app.runner.events.empty()) and time.monotonic() < deadline:
        app.update()
        time.sleep(.03)
    app.update()
    check(app.runner.last_result and app.runner.last_result['success'], 'desktop receives native backend completion')
    check('Completed: self-test' in app.session.get('1.0', 'end'), 'session records completion')
    if os.environ.get('WRONGWAY_SCREENSHOTS'):
        import subprocess
        out = Path(os.environ['WRONGWAY_SCREENSHOTS'])
        for frame, name in [(app.editor, 'editor'), (app.pcm, 'pcm'), (app.logtab, 'logs')]:
            app.book.select(frame)
            app.update()
            subprocess.run(['import', '-display', os.environ['DISPLAY'], '-window', str(app.winfo_id()), str(out / (name + '.png'))], check=True)
    print('GUI RESULT:', checks, 'checks passed')
finally:
    app.destroy()
