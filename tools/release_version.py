"""Release version of the .NET companion (dotnet/Directory.Build.props)."""
from pathlib import Path
import re

_PROPS = Path(__file__).resolve().parents[1] / "dotnet/Directory.Build.props"
VERSION = re.search(r"<Version>(\d+\.\d+\.\d+)</Version>", _PROPS.read_text(encoding="utf-8")).group(1)
