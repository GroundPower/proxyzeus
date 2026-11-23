"""Optional ENet handshake probe. Never sends a Growtopia login or credentials."""
import argparse
import ctypes
import pathlib
import time

parser = argparse.ArgumentParser()
parser.add_argument("host", help="IPv4 address, not hostname")
parser.add_argument("port", type=int)
parser.add_argument("--ubisoft", action="store_true")
args = parser.parse_args()
if not 1 <= args.port <= 65535:
    parser.error("port must be 1..65535")

root = pathlib.Path(__file__).resolve().parent.parent
lib = ctypes.CDLL(str(root / "build/native/Release/zeus_native.dll"))
lib.zeus_init.restype = ctypes.c_int
lib.zeus_open.argtypes = [ctypes.c_char_p, ctypes.c_uint16, ctypes.c_int, ctypes.c_int]
lib.zeus_open.restype = ctypes.c_void_p
lib.zeus_connect.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_uint16]
lib.zeus_connect.restype = ctypes.c_int
lib.zeus_poll.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int] + [ctypes.POINTER(ctypes.c_int)] * 4
lib.zeus_poll.restype = ctypes.c_int
lib.zeus_flush.argtypes = [ctypes.c_void_p]
lib.zeus_close.argtypes = [ctypes.c_void_p]
if lib.zeus_init() != 0:
    raise SystemExit("ENet initialization failed")
handle = lib.zeus_open(b"0.0.0.0", 0, int(args.ubisoft), 0)
if not handle:
    raise SystemExit("ENet host creation failed")
connected = False
message_types = []
try:
    if lib.zeus_connect(handle, args.host.encode("ascii"), args.port) != 0:
        raise SystemExit("ENet connection could not start")
    buffer = ctypes.create_string_buffer(4 * 1024 * 1024)
    event_type, length, channel, flags = [ctypes.c_int() for _ in range(4)]
    until = time.monotonic() + 7
    while time.monotonic() < until:
        result = lib.zeus_poll(handle, buffer, len(buffer), ctypes.byref(event_type), ctypes.byref(length), ctypes.byref(channel), ctypes.byref(flags))
        if result < 0:
            raise RuntimeError(f"poll failed: {result}")
        if result:
            if event_type.value == 1:
                connected = True
            if event_type.value == 3 and length.value >= 4:
                message_types.append(int.from_bytes(buffer.raw[:4], "little"))
            if event_type.value == 2 or (connected and message_types):
                break
        lib.zeus_flush(handle)
        time.sleep(0.002)
    print(f"ENet handshake: {'CONNECTED' if connected else 'TIMEOUT'}; received message types: {message_types}")
    print("No game login or credentials were sent.")
finally:
    lib.zeus_close(handle)
