"""Reads a SCIP index (https://github.com/sourcegraph/scip, scip.proto) without generated code.

Only the fields the question builder needs are decoded: each document's path and its occurrences
(symbol, range, roles, enclosing range). Field numbers are scip.proto's.
"""
from dataclasses import dataclass, field

DEFINITION = 0x1  # SymbolRole.Definition
IMPORT = 0x2  # SymbolRole.Import


@dataclass
class Occurrence:
    symbol: str
    roles: int
    # [start_line, start_char, end_line, end_char], 0-based; a three-number range ends on its start line.
    range: list[int]
    enclosing: list[int] | None = None

    @property
    def line(self) -> int:
        return self.range[0]


@dataclass
class Document:
    path: str
    occurrences: list[Occurrence] = field(default_factory=list)


def _varint(buf: bytes, i: int) -> tuple[int, int]:
    shift = value = 0
    while True:
        b = buf[i]
        i += 1
        value |= (b & 0x7F) << shift
        if b < 0x80:
            return value, i
        shift += 7


def _fields(buf: bytes):
    """Yields (field number, wire type, value) for one message; a length-delimited value is bytes."""
    i = 0
    while i < len(buf):
        key, i = _varint(buf, i)
        number, wire = key >> 3, key & 7
        if wire == 0:
            value, i = _varint(buf, i)
        elif wire == 2:
            size, i = _varint(buf, i)
            value, i = buf[i:i + size], i + size
        elif wire == 1:
            value, i = buf[i:i + 8], i + 8
        elif wire == 5:
            value, i = buf[i:i + 4], i + 4
        else:
            raise ValueError(f"unsupported wire type {wire}")
        yield number, wire, value


def _ints(wire: int, value) -> list[int]:
    """A repeated int32, packed (length-delimited) or not."""
    if wire == 0:
        return [value]
    out, i = [], 0
    while i < len(value):
        v, i = _varint(value, i)
        out.append(v)
    return out


def _range(r: list[int]) -> list[int]:
    return [r[0], r[1], r[0], r[2]] if len(r) == 3 else r


def _occurrence(buf: bytes) -> Occurrence:
    symbol, roles, rng, enclosing = "", 0, [], []
    for number, wire, value in _fields(buf):
        if number == 1:
            rng += _ints(wire, value)
        elif number == 2:
            symbol = value.decode("utf-8")
        elif number == 3:
            roles = value
        elif number == 7:
            enclosing += _ints(wire, value)
    return Occurrence(symbol, roles, _range(rng), _range(enclosing) if enclosing else None)


def _document(buf: bytes) -> Document:
    doc = Document("")
    for number, _, value in _fields(buf):
        if number == 1:
            doc.path = value.decode("utf-8")
        elif number == 2:
            doc.occurrences.append(_occurrence(value))
    return doc


def read(path: str) -> list[Document]:
    with open(path, "rb") as f:
        data = f.read()
    return [_document(value) for number, _, value in _fields(data) if number == 2]
