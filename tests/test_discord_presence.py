"""Synthetic Discord RPC peer: no desktop Discord or account traffic."""
import json
from pathlib import Path
import struct
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from vegas_reborn.discord_presence import APPLICATION_ID, DiscordPresence, FrameReader, activity_for, frame


class Pipe:
    def __init__(self):
        self.sent, self.replies = [], []
        self.closed = False

    def send(self, opcode, payload):
        self.sent.append((opcode, payload))

    def poll(self):
        result, self.replies = self.replies, []
        return result

    def close(self):
        self.closed = True

    def reply(self, **value):
        self.replies.append((1, json.dumps(value).encode()))


class PresenceTests(unittest.TestCase):
    def setUp(self):
        self.pipe = Pipe()
        self.messages = []
        self.presence = DiscordPresence(connector=lambda: self.pipe, notify=self.messages.append, start=False)
        self.addCleanup(self.presence.close)

    def ready(self):
        self.presence.set_activity(activity_for())
        self.presence._tick(0)
        self.assertEqual(len(self.pipe.sent), 1)  # Handshake must precede activity.
        self.assertEqual(json.loads(self.pipe.sent[0][1]), {"v": 1, "client_id": APPLICATION_ID})
        self.pipe.reply(evt="READY")
        self.presence._tick(.1)

    def test_fragmented_frames_and_boundaries(self):
        reader = FrameReader()
        data = frame(1, b'{"evt":"READY"}') + frame(3, b"ping")
        result = []
        for byte in data:
            result.extend(reader.feed(bytes([byte])))
        self.assertEqual(result, [(1, b'{"evt":"READY"}'), (3, b"ping")])
        for header in (struct.pack("<II", 1, 65537), struct.pack("<II", 5, 0)):
            with self.assertRaises(ValueError):
                FrameReader().feed(header)

    def test_activity_acknowledged_and_updates_throttled(self):
        self.ready()
        first = json.loads(self.pipe.sent[-1][1])
        self.assertEqual(first["cmd"], "SET_ACTIVITY")
        self.assertNotIn("Sharing activity with Discord", self.messages)
        self.pipe.reply(cmd="SET_ACTIVITY", nonce=first["nonce"])
        self.presence._tick(.2)
        self.assertIn("Sharing activity with Discord", self.messages)
        count = len(self.pipe.sent)
        self.presence.set_activity(activity_for(playing=True, started=123))
        self.presence._tick(.3)
        self.assertEqual(len(self.pipe.sent), count)
        self.presence._tick(2.2)
        activity = json.loads(self.pipe.sent[-1][1])["args"]["activity"]
        self.assertEqual(activity["timestamps"]["start"], 123)
        self.assertNotIn("secrets", activity)
        self.assertNotIn("party", activity)

    def test_privacy_toggle_clears_activity_and_disconnects(self):
        self.ready()
        self.presence.set_activity(None)
        self.presence._tick(.2)
        self.assertIsNone(json.loads(self.pipe.sent[-1][1])["args"]["activity"])
        self.assertTrue(self.pipe.closed)
        self.assertIsNone(self.presence.pipe)

    def test_ping_and_handshake_timeout(self):
        self.presence.set_activity(activity_for())
        self.presence._tick(0)
        self.pipe.replies.append((3, b"opaque ping"))
        self.presence._tick(1)
        self.assertEqual(self.pipe.sent[-1], (4, b"opaque ping"))
        with self.assertRaises(TimeoutError):
            self.presence._tick(11)

    def test_activity_errors_are_not_success(self):
        self.ready()
        self.pipe.reply(evt="ERROR", data={"message": "untrusted response"})
        with self.assertRaises(ValueError):
            self.presence._tick(.2)
        self.assertNotIn("Sharing activity with Discord", self.messages)
        self.assertNotIn("untrusted response", str(self.messages))

    def test_unacknowledged_activity_times_out(self):
        self.ready()
        with self.assertRaises(TimeoutError):
            self.presence._tick(11)

    def test_host_startup_is_distinct_from_running(self):
        self.assertEqual(activity_for(preparing_host=True)["state"], "Preparing dedicated server")
        self.assertEqual(activity_for(hosting=True)["state"], "Dedicated server running")


if __name__ == "__main__":
    unittest.main()
