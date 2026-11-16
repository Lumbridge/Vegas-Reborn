# Vegas Reborn companion

- This repository contains the Windows companion and native client adapters only.
- Keep backend services, databases, deployment scripts and infrastructure in the separate retail backend project.
- Never commit game binaries/data, captures, characters, account sessions, or private signing keys.
- The installed original game is read-only; use companion-owned copies and profiles.
- Use feature/, fix/ or enhancement/ branches, never codex/ or agent/.
- Ground original-game behavior in original code/data or actual retail traffic.
- Automated retail launches require an established startup-safe private-audio route for that test. App/updater tests do not require launching the retail game.
- Run python -m unittest discover -s tests -v for updater and transport changes. Synthetic tests do not establish retail gameplay interoperability.
- Publish only allowlisted companion files. Sign update manifests and preserve local game, account, preference and save directories during updates.
- Use plain, functional UI copy. Do not add quips, slogans or marketing language.
- Normal hosting belongs in the game's Create Match flow. Keep dedicated hosting out of normal companion navigation; retain the separate operator tool and explicit legacy CLI support.
- Switching pages must preserve the user's window size and position. Scroll content within the page when needed.
