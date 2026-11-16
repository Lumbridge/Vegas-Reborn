"""Local preferences, separate from account credentials and cloud saves."""
from dataclasses import asdict, dataclass
import json
from pathlib import Path
from .companion import DEFAULT_RETAIL, _private_write
from .display import MODES


@dataclass
class Preferences:
    game_folder: str = str(DEFAULT_RETAIL)
    display_mode: str = "Borderless"
    monitor_device: str = ""
    minimize_on_play: bool = True
    discord_presence: bool = True
    check_updates: bool = True

    @classmethod
    def load(cls, path):
        try:
            value = json.loads(Path(path).read_text(encoding="utf-8"))
            result = cls(**{key: value[key] for key in cls.__dataclass_fields__ if key in value})
            if (not isinstance(result.game_folder, str) or result.display_mode not in MODES
                    or not isinstance(result.monitor_device, str) or type(result.minimize_on_play) is not bool
                    or type(result.discord_presence) is not bool or type(result.check_updates) is not bool):
                return cls()
            return result
        except (OSError, ValueError, TypeError):
            return cls()

    def save(self, path):
        _private_write(path, (json.dumps(asdict(self), indent=2) + "\n").encode())
