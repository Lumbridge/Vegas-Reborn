"""Optional dedicated-host tool. Normal matches can be hosted in the game."""
import argparse
from pathlib import Path
import signal
import sys
import threading

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/"src"))
from vegas_reborn.companion import DEFAULT_RETAIL
from vegas_reborn.dedicated import run_dedicated


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-folder",type=Path,default=DEFAULT_RETAIL)
    parser.add_argument("--config",type=Path,default=ROOT/"local/companion/service.json")
    parser.add_argument("--session-file",type=Path,default=ROOT/"local/companion/account-session.json")
    args=parser.parse_args()
    stop=threading.Event()
    signal.signal(signal.SIGINT,lambda *_:stop.set())
    run_dedicated(args.session_file,source=args.game_folder,config_path=args.config,stop_event=stop,notify=print)


if __name__=="__main__":main()
