import unittest
import tempfile
import struct
from pathlib import Path
from unittest.mock import patch
from wrongway import core, logs


def fixture(size=1048576):
    """Synthetic image with upstream-compatible metadata; never a vehicle tune."""
    data = bytearray(size)
    data[0x3ffc:0x4000] = b'\x00\x00\x00\x01'
    data[0x1fffe:0x20000] = b'\x4a\xfc'
    data[-2:] = b'\x4a\xfc'
    data[0x504:0x508] = (12587603 if size == 1048576 else 12593358).to_bytes(4, 'big')
    struct.pack_into('>II', data, 0x50c, 0, size - 1)
    for i in range(1, 8):
        start = 0x8000 + (i - 1) * 0x1000
        struct.pack_into('>II', data, 0x50c + i * 8, start, start + 0xfff)
    return core.TuneImage(data)


class EditingTests(unittest.TestCase):
    def setUp(self):
        self.image = core.TuneImage(bytes(128))

    def test_signed_big_endian(self):
        p = core.Parameter(Address='0x2', Storage='Signed', ByteWidth=2, Scale=.5, Offset=-40)
        self.image.write(p, 0, 0, -45)
        self.assertEqual(self.image.data[2:4], b'\xff\xf6')
        self.assertEqual(self.image.read(p), -45)

    def test_unsigned_little_endian(self):
        p = core.Parameter(ByteWidth=4, LittleEndian=True)
        self.image.write(p, 0, 0, 0x12345678)
        self.assertEqual(self.image.data[:4], b'\x78\x56\x34\x12')
        self.assertEqual(self.image.read(p), 0x12345678)

    def test_float_endianness(self):
        for width, little, value in [(4, False, 1.25), (8, True, -1234.125)]:
            with self.subTest(width=width):
                p = core.Parameter(Storage='Float', ByteWidth=width, LittleEndian=little)
                self.image.write(p, 0, 0, value)
                self.assertEqual(self.image.read(p), value)

    def test_switch_preserves_other_bits(self):
        image = core.TuneImage(b'\xa5\x80')
        p = core.Parameter(Storage='Flag', ByteWidth=2, Bit=9)
        image.write(p, 0, 0, 1)
        self.assertEqual(image.data, b'\xa7\x80')
        image.write(p, 0, 0, 0)
        self.assertEqual(image.data, b'\xa5\x80')

    def test_table_offsets(self):
        p = core.Parameter(Address='0x20', Rows=3, Columns=4, ByteWidth=2)
        self.image.write(p, 2, 3, 0x1234)
        self.assertEqual(self.image.data[54:56], b'\x12\x34')

    def test_strided_offsets(self):
        p = core.Parameter(Address='0x20', Rows=3, Columns=4, ByteWidth=1, ColumnStride=2, RowStride=12)
        self.image.write(p, 2, 3, 50)
        self.assertEqual(self.image.data[62], 50)
        self.assertEqual(sum(self.image.data), 50)

    def test_undo_redo_exact(self):
        p = core.Parameter()
        self.image.write(p, 0, 0, 3)
        self.image.undo()
        self.assertEqual(self.image.data[0], 0)
        self.image.redo()
        self.assertEqual(self.image.data[0], 3)

    def test_new_edit_invalidates_redo(self):
        p = core.Parameter()
        self.image.write(p, 0, 0, 3)
        self.image.undo()
        self.image.write(p, 0, 0, 4)
        self.assertFalse(self.image.redo_stack)

    def test_batch_is_atomic(self):
        p = core.Parameter(Rows=2, Columns=2)
        with self.assertRaises(ValueError):
            self.image.batch(p, [(0, 0, 1), (1, 1, 300)])
        self.assertEqual(self.image.changed_bytes, 0)
        self.assertFalse(self.image.undo_stack)

    def test_batch_single_undo(self):
        p = core.Parameter(Rows=2, Columns=2)
        self.image.batch(p, [(0, 0, 1), (1, 1, 2)])
        self.image.undo()
        self.assertEqual(self.image.changed_bytes, 0)
        self.image.redo()
        self.assertEqual(self.image.changed_bytes, 2)

    def test_rejected_edits_leave_bytes_unchanged(self):
        for p, value in [(core.Parameter(Address='0x7f', ByteWidth=2), 1), (core.Parameter(), 256), (core.Parameter(Minimum=10), 9), (core.Parameter(), float('nan')), (core.Parameter(Storage='Flag'), 2), (core.Parameter(Storage='Float', ByteWidth=4), 1e99)]:
            with self.subTest(parameter=p, value=value):
                with self.assertRaises(ValueError):
                    self.image.write(p, 0, 0, value)
                self.assertEqual(self.image.changed_bytes, 0)

    def test_negative_half_rounds_away(self):
        p = core.Parameter(Storage='Signed')
        self.assertEqual(self.image.write(p, 0, 0, -1.5), -2)

    def test_limits_and_overlapping_strides(self):
        for p in [core.Parameter(Scale=0), core.Parameter(Minimum=5, Maximum=2), core.Parameter(Rows=513), core.Parameter(ByteWidth=2, ColumnStride=1), core.Parameter(Rows=2, Columns=2, RowStride=1), core.Parameter(LittleEndian='false')]:
            with self.subTest(parameter=p), self.assertRaises(ValueError):
                p.validate(128)

    def test_no_op_has_no_history(self):
        self.image.write(core.Parameter(), 0, 0, 0)
        self.assertFalse(self.image.undo_stack)

    def test_table_csv_and_tsv(self):
        p = core.Parameter(Rows=2, Columns=2)
        self.image.batch(p, core.parse_table('1\t2\n3\t4', p))
        self.assertEqual(core.export_table(self.image, p), '1,2\r\n3,4\r\n')
        with self.assertRaises(ValueError):
            core.parse_table('1,2,3', p)

    def test_affine_nested(self):
        self.assertEqual(core.affine('((X*.03125)-40)/2'), (.015625, -20))

    def test_affine_rejects_code_and_nonlinear(self):
        for expression in ['X*X', '1/X', 'X/0', '0*X+5', "__import__('os').system('id')", 'X**2', 'X.real']:
            with self.subTest(expression=expression), self.assertRaises((ValueError, SyntaxError)):
                core.affine(expression)


class FileAndChecksumTests(unittest.TestCase):
    def test_atomic_save_preserves_overwritten_file(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'test.bin'
            path.write_bytes(b'old')
            backup = core.atomic_save(path, b'new')
            self.assertEqual(path.read_bytes(), b'new')
            self.assertEqual(backup.read_bytes(), b'old')
            self.assertFalse(list(Path(folder).glob('.wrongway-*')))

    def test_save_failure_preserves_original(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'test.bin'
            path.write_bytes(b'old')
            with patch('wrongway.core.os.replace', side_effect=OSError('disk failure')):
                with self.assertRaises(OSError):
                    core.atomic_save(path, b'new')
            self.assertEqual(path.read_bytes(), b'old')
            self.assertFalse(list(Path(folder).glob('.wrongway-*')))

    def test_json_definitions_roundtrip(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'test.json'
            core.save_definitions(path, 'Test', '12587603', [core.Parameter()])
            document, params = core.load_definitions(path, 128)
            self.assertEqual(document['ExpectedOS'], '12587603')
            self.assertEqual(params[0], core.Parameter())

    def test_xdf_demo(self):
        path = Path(__file__).resolve().parents[1] / 'examples/demo.xdf'
        title, params, warnings = core.import_xdf(path, 128)
        self.assertEqual(len(params), 3)
        self.assertFalse(warnings)
        self.assertEqual(params[2].ColumnLabels, ['800', '1200', '1600', '2000'])

    def test_xdf_external_entities_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'test.xdf'
            path.write_text('<!DOCTYPE x [<!ENTITY a SYSTEM "file:///etc/passwd">]><XDFFORMAT/>')
            with self.assertRaises(ValueError):
                core.import_xdf(path, 128)

    def test_xdf_reports_unsupported(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'test.xdf'
            path.write_text('<XDFFORMAT><XDFHEADER><BASEOFFSET offset="4"/></XDFHEADER><XDFCONSTANT><title>Good</title><EMBEDDEDDATA mmedaddress="0" mmedelementsizebits="16" mmedtypeflags="3"/><MATH equation="X/2-40"/></XDFCONSTANT><XDFFUNCTION><title>Unsupported</title></XDFFUNCTION></XDFFORMAT>')
            _, params, warnings = core.import_xdf(path, 128)
            self.assertEqual(params[0].address, 4)
            self.assertEqual(params[0].Scale, .5)
            self.assertTrue(params[0].LittleEndian)
            self.assertEqual(len(warnings), 1)

    def test_p59_checksum_repair_and_undo(self):
        image = fixture()
        image.data[0x8004:0x8006] = b'\x12\x34'
        before = bytes(image.data)
        core.repair_checksums(image)
        self.assertTrue(all(s['valid'] for s in core.checksum_report(image.data)[2]))
        self.assertEqual(core.pcm_layout(image.data)[:2], ('P59', 12587603))
        image.undo()
        self.assertEqual(bytes(image.data), before)

    def test_p01_checksum_repair(self):
        image = fixture(524288)
        core.repair_checksums(image)
        self.assertTrue(all(s['valid'] for s in core.checksum_report(image.data)[2]))
        self.assertEqual(core.pcm_layout(image.data)[0], 'P01')

    def test_os_checksum_range_excludes_terminal_signature(self):
        for size in (524288, 1048576):
            with self.subTest(size=size):
                image = fixture(size)
                struct.pack_into('>I', image.data, 0x510, size - 3)
                core.repair_checksums(image)
                report = core.checksum_report(image.data)[2]
                self.assertTrue(all(s['valid'] for s in report))
                self.assertEqual(report[0]['end'], size - 3)
                self.assertEqual(image.data[-2:], b'\x4a\xfc')
                # A calibration edit must still fail validation until repaired.
                image.data[0x800a] ^= 1
                self.assertFalse(core.checksum_report(image.data)[2][1]['valid'])
                core.repair_checksums(image)
                self.assertTrue(all(s['valid'] for s in core.checksum_report(image.data)[2]))
                image.data[0x20010] ^= 1
                self.assertFalse(core.checksum_report(image.data)[2][0]['valid'])

    def test_shortened_os_range_remains_rejected(self):
        image = fixture()
        for end in (len(image.data) - 5, 0x3fff):
            with self.subTest(end=end):
                struct.pack_into('>I', image.data, 0x510, end)
                with self.assertRaises(ValueError):
                    core.checksum_report(image.data)

    def test_os_checksum_excludes_parameter_and_calibration_area(self):
        image = fixture()
        os_segment = core.pcm_layout(image.data)[2][0]
        checksum = core.segment_checksum(image.data, os_segment)
        image.data[0x4000] = 0x5a
        self.assertEqual(core.segment_checksum(image.data, os_segment), checksum)
        image.data[0x20000] = 0x5a
        self.assertNotEqual(core.segment_checksum(image.data, os_segment), checksum)

    def test_bad_signature_or_layout_rejected(self):
        for corrupt in ('size', 'tail', 'end', 'overlap'):
            image = fixture()
            if corrupt == 'size':
                image.data.pop()
            elif corrupt == 'tail':
                image.data[-1] = 0
            elif corrupt == 'end':
                struct.pack_into('>I', image.data, 0x518, 0xffffffff)
            else:
                struct.pack_into('>I', image.data, 0x51c, 0x8000)
            with self.subTest(corrupt=corrupt), self.assertRaises(ValueError):
                core.repair_checksums(image)


class LogTests(unittest.TestCase):
    def test_user_afr_conversion(self):
        self.assertAlmostEqual(logs.calculate(logs.AFR_EXPRESSION, {'7101.10': '2.5'}), 14.57)

    def test_custom_math_bracket_names(self):
        self.assertEqual(logs.calculate('([RPM] + [MAP (kPa)]) / 2', {'RPM': '1000', 'MAP (kPa)': '40'}), 520)

    def test_math_rejects_code(self):
        for expression in ['__import__("os")', '[RPM].real', '2**100', '[RPM][0]']:
            with self.subTest(expression=expression), self.assertRaises((ValueError, SyntaxError)):
                logs.calculate(expression, {'RPM': 1})

    def test_invalid_math_rows_remain_blank(self):
        headers, rows, missing = logs.add_math(['7101.10'], [{'7101.10': '2.5'}, {'7101.10': ''}], 'AFR', logs.AFR_EXPRESSION)
        self.assertEqual(rows[0]['AFR'], '14.57')
        self.assertEqual(rows[1]['AFR'], '')
        self.assertEqual(missing, 1)

    def test_valid_formula_with_zero_denominator_row(self):
        _, rows, missing = logs.add_math(['X'], [{'X': 1}, {'X': 3}], 'Result', '1/([X]-1)')
        self.assertEqual(rows[0]['Result'], '')
        self.assertEqual(rows[1]['Result'], '0.5')
        self.assertEqual(missing, 1)

    def test_missing_column_and_duplicate_math_name(self):
        for name, expression in [('RPM', '[RPM]'), ('AFR', '[missing]')]:
            with self.subTest(name=name), self.assertRaises(ValueError):
                logs.add_math(['RPM'], [{'RPM': 1}], name, expression)

    def test_histogram_bins_and_missing_rows(self):
        rows = [{'rpm': 810, 'map': 42, 'afr': 14}, {'rpm': 950, 'map': 45, 'afr': 15}, {'rpm': '', 'map': 10, 'afr': 8}]
        result = logs.histogram(rows, 'rpm', 'map', 'afr', 400, 10)
        self.assertEqual(result, [{'x': 800, 'y': 40, 'count': 2, 'mean': 14.5, 'minimum': 14., 'maximum': 15.}])

    def test_histogram_rejects_zero_bin_width(self):
        with self.assertRaises(ValueError):
            logs.histogram([], 'x', 'y', 'z', 0, 10)

    def test_csv_duplicate_headers_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'log.csv'
            path.write_text('RPM,RPM\n1,2\n')
            with self.assertRaises(ValueError):
                logs.load_csv(path)

    def test_vcm_time_window_methods_and_boolean_operators(self):
        headers = ['Time (s)', 'Throttle Position (%)', 'Commanded Equivalence Ratio', 'DFCO Active']
        rows = [
            {'Time (s)': '0.0', 'Throttle Position (%)': '20', 'Commanded Equivalence Ratio': '1.00', 'DFCO Active': '0'},
            {'Time (s)': '0.5', 'Throttle Position (%)': '20', 'Commanded Equivalence Ratio': '1.00', 'DFCO Active': '0'},
            {'Time (s)': '1.0', 'Throttle Position (%)': '20', 'Commanded Equivalence Ratio': '1.00', 'DFCO Active': '0'},
            {'Time (s)': '1.5', 'Throttle Position (%)': '45', 'Commanded Equivalence Ratio': '0.85', 'DFCO Active': '0'},
        ]
        cl_expr = logs.VCM_TELEMETRY_PRESETS['CL Filter']
        _, out_rows, missing = logs.add_math(headers, rows, 'CL_Pass', cl_expr)
        self.assertEqual(missing, 0)
        # At t=0.5s, TPS is steady (slope=0), DFCO=0, EqRatio=1.00 -> CL Filter is 1
        self.assertEqual(out_rows[1]['CL_Pass'], '1')
        # At t=1.5s, TPS jumped and EqRatio=0.85 -> CL Filter is 0
        self.assertEqual(out_rows[3]['CL_Pass'], '0')

    def test_vcm_derivative_integral_delta_and_prev(self):
        headers = ['Time (s)', 'Vehicle Speed (MPH)']
        rows = [
            {'Time (s)': '0.0', 'Vehicle Speed (MPH)': '10'},
            {'Time (s)': '1.0', 'Vehicle Speed (MPH)': '20'},
            {'Time (s)': '2.0', 'Vehicle Speed (MPH)': '35'},
        ]
        _, rows_d, _ = logs.add_math(headers, rows, 'Accel', '[Vehicle Speed (MPH).derivative()]')
        self.assertEqual(rows_d[1]['Accel'], '10')
        self.assertEqual(rows_d[2]['Accel'], '15')

        _, rows_i, _ = logs.add_math(headers, rows, 'Dist', '[Vehicle Speed (MPH).integral()]')
        self.assertEqual(rows_i[1]['Dist'], '15')
        self.assertEqual(rows_i[2]['Dist'], '42.5')

        _, rows_delta, _ = logs.add_math(headers, rows, 'Step', '[Vehicle Speed (MPH).delta()]')
        self.assertEqual(rows_delta[2]['Step'], '15')

        _, rows_prev, _ = logs.add_math(headers, rows, 'PrevSpeed', '[Vehicle Speed (MPH).prev()]')
        self.assertEqual(rows_prev[2]['PrevSpeed'], '20')

    def test_vcm_math_xml_import_and_filtered_histogram(self):
        xml_text = (
            '<?xml version="1.0" encoding="utf-8"?>'
            '<MathParameter Name="CL Steady" Units="bool" '
            'Expression="[50090.156.slope(500)] &lt; 2 and [50118.238] = 1.00" />'
        )
        with tempfile.TemporaryDirectory() as folder:
            xml_path = Path(folder) / 'CL.MathParameter.xml'
            xml_path.write_text(xml_text)
            spec = logs.load_vcm_math_xml(xml_path)
            self.assertEqual(spec['name'], 'CL Steady')
            self.assertIn('< 2 and', spec['expression'])

        rows = [
            {'Time (s)': '0.0', 'RPM': '1600', 'MAP': '50', 'LTFT': '2.0', 'Throttle Position (%)': '25', 'Commanded Equivalence Ratio': '1.00'},
            {'Time (s)': '0.5', 'RPM': '1650', 'MAP': '52', 'LTFT': '4.0', 'Throttle Position (%)': '25', 'Commanded Equivalence Ratio': '1.00'},
            {'Time (s)': '1.0', 'RPM': '1700', 'MAP': '54', 'LTFT': '-15.0', 'Throttle Position (%)': '80', 'Commanded Equivalence Ratio': '0.82'},
        ]
        bins = logs.histogram(rows, 'RPM', 'MAP', 'LTFT', 400, 10, filter_expression=spec['expression'])
        self.assertEqual(len(bins), 1)
        self.assertEqual(bins[0]['count'], 2)
        self.assertAlmostEqual(bins[0]['mean'], 3.0)


if __name__ == '__main__':
    unittest.main()
