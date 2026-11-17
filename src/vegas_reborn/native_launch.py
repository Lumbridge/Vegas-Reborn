"""Actionable failures for required native launch components; no secrets logged."""
from pathlib import Path
import json
import subprocess


class NativeLaunchError(Exception):
    pass


def component_error(name,error):
    code=getattr(error,'winerror',None)
    if code in (225,226):
        return (f'Windows Security blocked or quarantined {name} (Windows error {code}).\n\n'
                'This required launch component cannot run. Review the detection in Windows Security > '
                'Virus & threat protection > Protection history. Repeated sign-in will not repair it. '
                'Your installed game and local character were preserved.')
    if isinstance(error,FileNotFoundError) or code in (2,3):
        return (f'Required launch component is missing: {name}.\n\n'
                'The installation is incomplete, or security software removed this file. '
                'Check Windows Security > Protection history, then reinstall a complete official release once the file detection has been addressed. '
                'Your installed game and local character were preserved.')
    if isinstance(error,PermissionError) or code==5:
        return f'Windows denied access to the required launch component {name}. Check its file access and security-software history. Your local character was preserved.'
    detail=f' (Windows error {code})' if code is not None else ''
    return f'Could not access the required launch component {name}{detail}. Your installed game and local character were preserved.'


def require_components(directory,native_version=13):
    for name in ('VegasSteamControl.exe','VegasSteamGate.dll','VegasInjector.exe',f'VegasCompanion-{native_version}.dll'):
        try:
            with (Path(directory)/name).open('rb') as stream:
                if not stream.read(1):
                    raise NativeLaunchError(f'Required launch component is empty: {name}. Reinstall a complete official release. Your local character was preserved.')
        except OSError as error:
            raise NativeLaunchError(component_error(name,error)) from None


def run_component(arguments):
    name=Path(arguments[0]).name
    try:
        response=subprocess.run([str(item) for item in arguments],capture_output=True,timeout=40,
                                creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
    except OSError as error:
        raise NativeLaunchError(component_error(name,error)) from None
    except subprocess.TimeoutExpired:
        raise NativeLaunchError(f'{name} did not finish launch setup within 40 seconds. Your local character was preserved.') from None
    if response.returncode:
        raise NativeLaunchError(f'{name} failed during launch setup (exit code {response.returncode}). Your installed game and local character were preserved.')
    try:
        value=json.loads(response.stdout)
        if not isinstance(value,dict):raise ValueError()
        return value
    except (ValueError,TypeError):
        raise NativeLaunchError(f'{name} returned an invalid launch result. Your local character was preserved.') from None
