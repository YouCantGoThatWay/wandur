#!/usr/bin/env python3
"""Publish and install the local discovery executable as a daily user launch agent."""
import os
from pathlib import Path
import plistlib
import shutil
import subprocess
import sys


def main():
    if sys.platform != "darwin":
        raise SystemExit("This installer is for macOS. On other systems, schedule the worker's --once command daily.")
    root = Path(__file__).resolve().parents[1]
    dotnet = shutil.which("dotnet")
    if not dotnet:
        raise SystemExit("Install the .NET 10 runtime/SDK first.")
    output = root / "artifacts" / "discovery-worker"
    subprocess.run([dotnet, "publish", str(root / "src/Wandur.Discovery.Worker/Wandur.Discovery.Worker.csproj"),
                    "-c", "Release", "--no-restore", "--no-self-contained", "-o", str(output)], check=True)
    label = "net.wandur.discovery"
    folder = Path.home() / "Library" / "LaunchAgents"
    folder.mkdir(parents=True, exist_ok=True)
    logs = Path.home() / "Library" / "Logs" / "Wandur"
    logs.mkdir(parents=True, exist_ok=True)
    path = folder / (label + ".plist")
    config = {
        "Label": label,
        "ProgramArguments": [dotnet, str(output / "Wandur.Discovery.Worker.dll"), "--cache-dir", str(root / "directory-server/cache"), "--once"],
        "WorkingDirectory": str(root), "RunAtLoad": True, "StartInterval": 86400,
        "ProcessType": "Background", "LowPriorityIO": True,
        "StandardOutPath": str(logs / "discovery.log"), "StandardErrorPath": str(logs / "discovery-errors.log"),
    }
    domain = "gui/" + str(os.getuid())
    service = domain + "/" + label
    if subprocess.run(["launchctl", "print", service], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0:
        subprocess.run(["launchctl", "bootout", service], check=True)
    temporary = path.with_suffix(".tmp")
    temporary.write_bytes(plistlib.dumps(config))
    temporary.chmod(0o600)
    temporary.replace(path)
    subprocess.run(["launchctl", "bootstrap", domain, str(path)], check=True)
    print("Installed daily discovery worker:", path)
    print("Logs:", logs / "discovery.log")


if __name__ == "__main__":
    main()
