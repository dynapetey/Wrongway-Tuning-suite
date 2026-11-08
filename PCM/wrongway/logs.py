"""CSV math and histogram tools. User expressions are parsed, never eval'd."""
import ast
import csv
import math
import re
from .core import finite

AFR_EXPRESSION = '(([7101.10]/.5)+9.57)'


def parse_math(expression):
    aliases = {}
    def alias(match):
        name = 'column_' + str(len(aliases))
        aliases[name] = match.group(1)
        return name
    text = re.sub(r'\[([^\]]+)\]', alias, expression)
    if len(text) > 4096:
        raise ValueError('Formula is too long.')
    tree = ast.parse(text.strip(), mode='eval')
    if sum(1 for _ in ast.walk(tree)) > 200:
        raise ValueError('Formula is too complex.')
    for node in ast.walk(tree):
        if not isinstance(node, (ast.Expression, ast.Constant, ast.Name, ast.Load, ast.BinOp, ast.UnaryOp, ast.Add, ast.Sub, ast.Mult, ast.Div, ast.UAdd, ast.USub)):
            raise ValueError('Use numbers, [column names], +, -, *, / and parentheses.')
        if isinstance(node, ast.Name) and node.id not in aliases:
            raise ValueError('Surround a column name with [brackets].')
        if isinstance(node, ast.Constant) and type(node.value) not in (int, float):
            raise ValueError('Only numeric constants are supported.')
    return tree, aliases


def calculate(expression, row):
    tree, aliases = parse_math(expression)
    def visit(node):
        if isinstance(node, ast.Expression):
            return visit(node.body)
        if isinstance(node, ast.Constant) and type(node.value) in (int, float):
            return finite(node.value)
        if isinstance(node, ast.Name) and node.id in aliases:
            return finite(row[aliases[node.id]])
        if isinstance(node, ast.UnaryOp) and isinstance(node.op, (ast.UAdd, ast.USub)):
            return (-1 if isinstance(node.op, ast.USub) else 1) * visit(node.operand)
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
        raise ValueError('Use numbers, [column names], +, -, *, / and parentheses.')
    return finite(visit(tree))


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


def add_math(headers, rows, name, expression):
    if not name.strip() or name in headers:
        raise ValueError('Choose a new column name.')
    result, missing = [], 0
    # Validate syntax without evaluating a guessed denominator.
    names = re.findall(r'\[([^\]]+)\]', expression)
    for key in names:
        if key not in headers:
            raise ValueError('Missing source column: ' + key)
    parse_math(expression)
    for row in rows:
        new = dict(row)
        try:
            new[name] = format(calculate(expression, row), '.10g')
        except (ValueError, ZeroDivisionError, TypeError, KeyError):
            new[name] = ''
            missing += 1
        result.append(new)
    return headers + [name], result, missing


def histogram(rows, x, y, value, x_step, y_step):
    x_step, y_step = finite(x_step), finite(y_step)
    if x_step <= 0 or y_step <= 0:
        raise ValueError('Bin widths must be positive.')
    buckets = {}
    for row in rows:
        try:
            a, b, v = finite(row[x]), finite(row[y]), finite(row[value])
            key = math.floor(a / x_step) * x_step, math.floor(b / y_step) * y_step
            buckets.setdefault(key, []).append(v)
        except (ValueError, KeyError, TypeError):
            continue
    return [{'x': key[0], 'y': key[1], 'count': len(vals), 'mean': sum(vals) / len(vals), 'minimum': min(vals), 'maximum': max(vals)} for key, vals in sorted(buckets.items())]
