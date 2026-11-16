"""Native Discord desktop IPC, matching the UE5 FVegasDiscordRpc integration.

No OAuth bearer or native game credential is sent over this connection. Retail
join secrets are deliberately absent until a retail join path is verified.
"""
import ctypes as c
from ctypes import wintypes as w
import json
import os
import struct
import threading
import time

APPLICATION_ID = "1571611222636236904"  # Shared with UE5 VegasAccount/DefaultGame.ini.
MAX_PAYLOAD = 65536


def frame(opcode, payload):
    if len(payload) > MAX_PAYLOAD:
        raise ValueError("Discord IPC payload too large")
    return struct.pack("<II", opcode, len(payload)) + payload


class FrameReader:
    def __init__(self):
        self.buffer = bytearray()

    def feed(self, data):
        self.buffer.extend(data)
        frames = []
        while len(self.buffer) >= 8:
            opcode, size = struct.unpack_from("<II", self.buffer)
            if size > MAX_PAYLOAD or opcode > 4:
                raise ValueError("Invalid Discord IPC frame")
            if len(self.buffer) < size + 8:
                break
            frames.append((opcode, bytes(self.buffer[8:8+size])))
            del self.buffer[:8+size]
        return frames


class WindowsPipe:
    """Overlapped I/O with cancellation, so a hung Discord cannot hold the UI."""
    def __init__(self, path):
        self.k = k = c.WinDLL("kernel32", use_last_error=True)
        class Overlapped(c.Structure):
            _fields_ = [("internal", c.c_size_t), ("internal_high", c.c_size_t),
                        ("offset", w.DWORD), ("offset_high", w.DWORD), ("event", w.HANDLE)]
        self.Overlapped = Overlapped
        signatures = {
            "CreateFileW": ([w.LPCWSTR, w.DWORD, w.DWORD, c.c_void_p, w.DWORD, w.DWORD, w.HANDLE], w.HANDLE),
            "CreateEventW": ([c.c_void_p, w.BOOL, w.BOOL, w.LPCWSTR], w.HANDLE),
            "ReadFile": ([w.HANDLE, c.c_void_p, w.DWORD, c.POINTER(w.DWORD), c.POINTER(Overlapped)], w.BOOL),
            "WriteFile": ([w.HANDLE, c.c_void_p, w.DWORD, c.POINTER(w.DWORD), c.POINTER(Overlapped)], w.BOOL),
            "PeekNamedPipe": ([w.HANDLE, c.c_void_p, w.DWORD, c.POINTER(w.DWORD), c.POINTER(w.DWORD), c.POINTER(w.DWORD)], w.BOOL),
            "CancelIoEx": ([w.HANDLE, c.POINTER(Overlapped)], w.BOOL),
            "GetOverlappedResult": ([w.HANDLE, c.POINTER(Overlapped), c.POINTER(w.DWORD), w.BOOL], w.BOOL),
            "WaitForSingleObject": ([w.HANDLE, w.DWORD], w.DWORD),
            "CloseHandle": ([w.HANDLE], w.BOOL),
        }
        for name, (args, result) in signatures.items():
            fn = getattr(k, name)
            fn.argtypes, fn.restype = args, result
        self.handle = k.CreateFileW(path, 0xc0000000, 0, None, 3, 0x40000000, None)
        if self.handle == c.c_void_p(-1).value:
            raise c.WinError(c.get_last_error())
        self.reader = FrameReader()

    def _io(self, buffer, size, writing):
        operation = self.Overlapped()
        operation.event = self.k.CreateEventW(None, True, False, None)
        if not operation.event:
            raise c.WinError(c.get_last_error())
        count = w.DWORD()
        try:
            fn = self.k.WriteFile if writing else self.k.ReadFile
            ok = fn(self.handle, buffer, size, c.byref(count), c.byref(operation))
            if not ok:
                if c.get_last_error() != 997:
                    raise c.WinError(c.get_last_error())
                if self.k.WaitForSingleObject(operation.event, 200) != 0:
                    self.k.CancelIoEx(self.handle, c.byref(operation))
                    self.k.GetOverlappedResult(self.handle, c.byref(operation), c.byref(count), True)
                    raise TimeoutError("Discord IPC timed out")
                if not self.k.GetOverlappedResult(self.handle, c.byref(operation), c.byref(count), False):
                    raise c.WinError(c.get_last_error())
            if writing and count.value != size:
                raise OSError("Incomplete Discord IPC write")
            return count.value
        finally:
            self.k.CloseHandle(operation.event)

    def send(self, opcode, payload):
        data = frame(opcode, payload)
        self._io(c.create_string_buffer(data), len(data), True)

    def poll(self):
        available = w.DWORD()
        if not self.k.PeekNamedPipe(self.handle, None, 0, None, c.byref(available), None):
            raise c.WinError(c.get_last_error())
        if not available.value:
            return []
        size = min(available.value, MAX_PAYLOAD + 8)
        buffer = c.create_string_buffer(size)
        received = self._io(buffer, size, False)
        if not received:
            raise OSError("Discord IPC closed")
        return self.reader.feed(buffer.raw[:received])

    def close(self):
        if self.handle is not None:
            self.k.CancelIoEx(self.handle, None)
            self.k.CloseHandle(self.handle)
            self.handle = None


def connect_desktop():
    if os.name != "nt":
        raise OSError("Discord desktop integration requires Windows")
    for index in range(10):
        try:
            return WindowsPipe(r"\\.\pipe\discord-ipc-" + str(index))
        except OSError:
            continue
    raise OSError("Discord desktop unavailable")


def activity_for(*, playing=False, hosting=False, preparing_host=False, started=None):
    activity = {"details": "Original PC game" if playing else "Vegas Reborn Companion",
                "state": "Playing Rainbow Six Vegas" if playing else "Ready to play"}
    if hosting:
        activity["state"] = "Playing Â· Dedicated server running" if playing else "Dedicated server running"
    elif preparing_host:
        activity["state"] = "Playing Â· Preparing dedicated server" if playing else "Preparing dedicated server"
    if playing and started:
        activity["timestamps"] = {"start": int(started)}
    return activity


class DiscordPresence:
    def __init__(self, *, notify=lambda text: None, connector=connect_desktop, start=True):
        self.notify, self.connector = notify, connector
        self.lock, self.stop = threading.Lock(), threading.Event()
        self.desired = None
        self.pipe, self.ready = None, False
        self.sent, self.pending = None, None
        self.next_connect = self.next_send = self.handshake_deadline = 0
        self.nonce = 0
        self.status = None
        self.thread = None
        if start:
            self.thread = threading.Thread(target=self._run, name="VegasDiscordIPC", daemon=True)
            self.thread.start()

    def set_activity(self, activity):
        with self.lock:
            self.desired = json.dumps(activity, ensure_ascii=False, separators=(",", ":")) if activity else None

    def _status(self, value):
        if self.status != value:
            self.status = value
            self.notify(value)

    def _disconnect(self):
        if self.pipe:
            self.pipe.close()
        self.pipe, self.ready, self.sent, self.pending = None, False, None, None

    def _send_activity(self, value):
        self.nonce += 1
        self.pipe.send(1, ('{"cmd":"SET_ACTIVITY","args":{"pid":' + str(os.getpid())
                           + ',"activity":' + (value or "null") + '},"nonce":"' + str(self.nonce) + '"}').encode())

    def _tick(self, now):
        with self.lock:
            desired = self.desired
        if desired is None:
            if self.pipe and self.ready:
                self._send_activity(None)
            self._disconnect()
            self._status("Discord activity sharing off")
            return
        if not self.pipe:
            if now < self.next_connect:
                return
            self.next_connect = now + 5
            self._status("Open Discord desktop to share your activity")
            self.pipe = self.connector()
            self.pipe.send(0, json.dumps({"v": 1, "client_id": APPLICATION_ID}).encode())
            self.handshake_deadline = now + 10
            self._status("Connecting to Discord desktopâ€¦")
        for opcode, payload in self.pipe.poll():
            if opcode == 3:
                self.pipe.send(4, payload)
            elif opcode == 2:
                raise OSError("Discord IPC closed")
            elif opcode == 1:
                reply = json.loads(payload)
                if not isinstance(reply, dict):
                    raise ValueError("Invalid Discord reply")
                event = reply.get("evt")
                if event == "READY":
                    self.ready = True
                    self._status("Discord desktop connected")
                elif event == "ERROR":
                    raise ValueError("Discord rejected activity")
                elif self.pending and reply.get("nonce") == self.pending[0] and reply.get("cmd") == "SET_ACTIVITY":
                    self.sent = self.pending[1]
                    self.pending = None
                    self._status("Sharing activity with Discord")
        if not self.ready and now > self.handshake_deadline:
            raise TimeoutError("Discord handshake timed out")
        if self.pending and now > self.pending[2]:
            raise TimeoutError("Discord activity acknowledgement timed out")
        if self.ready and not self.pending and desired != self.sent and now >= self.next_send:
            self._send_activity(desired)
            self.pending = (str(self.nonce), desired, now + 10)
            self.next_send = now + 2

    def _run(self):
        try:
            while not self.stop.is_set():
                try:
                    self._tick(time.monotonic())
                except (OSError, ValueError, TypeError):
                    self._disconnect()
                    self.next_connect = time.monotonic() + 5
                    self._status("Discord unavailable â€” play continues normally")
                self.stop.wait(.05)
        finally:
            try:
                if self.pipe and self.ready:
                    self._send_activity(None)
            except OSError:
                pass
            self._disconnect()

    def close(self):
        self.stop.set()
        if self.thread:
            self.thread.join(timeout=2)
        else:
            self._disconnect()
