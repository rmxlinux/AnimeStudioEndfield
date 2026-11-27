#!/usr/bin/env python3
"""Independent reference for the encrypted Endfield VFS block stream.

This script is deliberately written from the public algorithm description and
does not share code with the C# implementation.  It reads the S-box straight
out of CryptoHelper.cs so the table can never drift, then:

* regenerates AnimeStudio.Tests/Data/stream-vectors.json (ciphertext produced
  by this reference, plaintext asserted by the .NET tests), and
* optionally re-checks the fixtures that were captured from the verified
  UnityPlayer implementation (block-zero / block-nonzero-offset).

Usage:
    python tools/endfield_stream_reference.py            # regenerate vectors
    python tools/endfield_stream_reference.py --check    # also verify fixtures
"""

import argparse
import json
import os
import re
import struct
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CRYPTO_HELPER = os.path.join(ROOT, "AnimeStudio", "Crypto", "CryptoHelper.cs")
DATA_DIR = os.path.join(ROOT, "AnimeStudio.Tests", "Data")

SEED = [
    0xA95E12C7 ^ 0x6AE642EB,
    0x8833DB04 ^ 0xA5C31E90,
    0x49770EF2 ^ 0x27B4D6F8,
    0x931CBA65 ^ 0xA5C31E90,
]

RCON = [0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80, 0x1B, 0x36]


def load_sbox():
    with open(CRYPTO_HELPER, "r", encoding="utf-8") as handle:
        text = handle.read()

    match = re.search(r"VFSAESSBox\s*=\s*new byte\[\]\s*\{(.*?)\}", text, re.S)
    if not match:
        raise SystemExit("VFSAESSBox not found in CryptoHelper.cs")

    values = [int(item, 16) for item in re.findall(r"0x([0-9A-Fa-f]{1,2})", match.group(1))]
    if len(values) != 256:
        raise SystemExit(f"expected 256 S-box entries, found {len(values)}")
    return values


SBOX = load_sbox()


def xtime(value):
    value <<= 1
    if value & 0x100:
        value ^= 0x11B
    return value & 0xFF


def expand_key(key):
    if len(key) != 16:
        raise ValueError("key must be 16 bytes")

    words = [list(key[4 * i:4 * i + 4]) for i in range(4)]
    rcon_index = 0

    while len(words) < 12:
        temp = list(words[-1])
        if len(words) % 4 == 0:
            temp = [SBOX[temp[1]], SBOX[temp[2]], SBOX[temp[3]], SBOX[temp[0]]]
            temp[0] ^= RCON[rcon_index]
            rcon_index += 1
        words.append([words[len(words) - 4][i] ^ temp[i] for i in range(4)])

    return words


def sub_bytes(state):
    for row in range(4):
        for column in range(4):
            state[row][column] = SBOX[state[row][column]]


def shift_rows(state):
    for row in range(1, 4):
        original = [state[row][column] for column in range(4)]
        for column in range(4):
            state[row][column] = original[(column + row) % 4]


def mix_columns(state):
    for column in range(4):
        a0, a1, a2, a3 = (state[row][column] for row in range(4))
        state[0][column] = xtime(a0) ^ (xtime(a1) ^ a1) ^ a2 ^ a3
        state[1][column] = a0 ^ xtime(a1) ^ (xtime(a2) ^ a2) ^ a3
        state[2][column] = a0 ^ a1 ^ xtime(a2) ^ (xtime(a3) ^ a3)
        state[3][column] = (xtime(a0) ^ a0) ^ a1 ^ a2 ^ xtime(a3)


def add_round_key(state, words, round_index):
    for column in range(4):
        word = words[round_index * 4 + column]
        for row in range(4):
            state[row][column] ^= word[row]


def encrypt_block(block, words):
    state = [[block[column * 4 + row] ^ words[column][row] for column in range(4)] for row in range(4)]

    sub_bytes(state)
    shift_rows(state)
    mix_columns(state)
    add_round_key(state, words, 1)

    sub_bytes(state)
    shift_rows(state)
    add_round_key(state, words, 2)

    return bytes(state[row][column] for column in range(4) for row in range(4))


def counter_block(counter):
    return struct.pack("<IIII", SEED[0], SEED[1], SEED[2], SEED[3] ^ counter)


def transform(data, key, offset):
    """XOR data with the stream keystream anchored at `offset`."""
    words = expand_key(key)
    result = bytearray(data)
    absolute = offset
    index = 0

    while index < len(result):
        keystream = encrypt_block(counter_block(absolute >> 4), words)
        position = absolute & 0xF
        take = min(16 - position, len(result) - index)
        for i in range(take):
            result[index + i] ^= keystream[position + i]
        index += take
        absolute += take

    return bytes(result)


def pattern(length, seed=0x5A):
    value = seed
    out = bytearray(length)
    for i in range(length):
        value = (value * 251 + 197) & 0xFF
        out[i] = value
    return bytes(out)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true", help="also verify the captured UnityPlayer fixtures")
    args = parser.parse_args()

    key_a = bytes(range(16))
    key_b = bytes.fromhex("8F1D2C4B6A7988A7B6C5D4E3F2011324")

    cases = [
        ("offset-0-16", key_a, 0, pattern(16, 0x11)),
        ("offset-0-1000", key_a, 0, bytes((i * 7 + 3) & 0xFF for i in range(1000))),
        ("offset-1-33", key_a, 1, pattern(33, 0x22)),
        ("offset-15-32", key_a, 15, pattern(32, 0x33)),
        ("offset-16-48", key_a, 16, pattern(48, 0x44)),
        ("offset-37-200", key_a, 37, pattern(200, 0x55)),
        ("offset-100-300", key_b, 100, pattern(300, 0x66)),
        ("offset-255-64", key_b, 255, pattern(64, 0x77)),
        ("offset-4096-512", key_b, 4096, pattern(512, 0x88)),
    ]

    vectors = {"generatedBy": "tools/endfield_stream_reference.py", "cases": []}
    for name, key, offset, plaintext in cases:
        ciphertext = transform(plaintext, key, offset)
        vectors["cases"].append({
            "name": name,
            "key": key.hex(),
            "offset": offset,
            "ciphertext": ciphertext.hex(),
            "plaintext": plaintext.hex(),
        })

    os.makedirs(DATA_DIR, exist_ok=True)
    path = os.path.join(DATA_DIR, "stream-vectors.json")
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(vectors, handle, indent=2)
        handle.write("\n")

    print(f"wrote {len(vectors['cases'])} cases to {path}")

    if args.check:
        ok = True
        for name, offset in (("block-zero", 0), ("block-nonzero-offset", 37)):
            input_path = os.path.join(DATA_DIR, name + ".input")
            expected_path = os.path.join(DATA_DIR, name + ".expected")
            if not (os.path.exists(input_path) and os.path.exists(expected_path)):
                print(f"SKIP {name}: fixture missing")
                continue

            with open(input_path, "rb") as handle:
                ciphertext = handle.read()
            with open(expected_path, "rb") as handle:
                expected = handle.read()

            actual = transform(ciphertext, key_a, offset)
            match = actual == expected
            ok = ok and match
            print(f"{'OK  ' if match else 'FAIL'} {name}: reference reproduces the captured fixture")

        return 0 if ok else 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
