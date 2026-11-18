"""Run IndieRun checks in isolated generated fixtures on Windows."""
from pathlib import Path
from datetime import datetime
import argparse
import json
import re
import subprocess
import tempfile
import xml.etree.ElementTree as ET


def main() -> int:
    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("suites", nargs="*", help="Names such as RestoreFlowChecks")
    parser.add_argument("--all", action="store_true", help="Run every *Checks project")
    parser.add_argument("--output", type=Path, default=root / "artifacts/checks" / datetime.now().strftime("%Y%m%d-%H%M%S"))
    parser.add_argument("--timeout", type=int, default=600)
    options = parser.parse_args()
    if options.timeout <= 0:
        parser.error("시간 제한은 0보다 커야 합니다.")
    available = {p.stem: p for p in (root / "tests").glob("*Checks/*Checks.csproj")}
    if options.all and options.suites:
        parser.error("--all과 개별 테스트 이름은 함께 사용할 수 없습니다.")
    names = sorted(available) if options.all else options.suites or ["CompositionChecks", "RestoreFlowChecks", "StatusChecks"]
    names = list(dict.fromkeys(names))
    if any(name not in available for name in names):
        parser.error("알 수 없는 테스트 이름입니다.")
    output = options.output.resolve()
    if output.exists():
        parser.error("결과는 존재하지 않는 새 폴더에 저장하세요.")
    output.mkdir(parents=True)
    fixtures = Path(tempfile.mkdtemp(prefix="ir-checks-"))
    results = []

    def build(project: Path, log: Path) -> bool:
        with log.open("w", encoding="utf-8") as stream:
            return subprocess.run(["dotnet", "build", str(project), "--nologo", "-v:q"], cwd=root,
                                  stdout=stream, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW).returncode == 0

    for name in names:
        print("CHECK " + name, flush=True)
        project = available[name]
        if not build(project, output / (name + "-build.log")):
            results.append({"name": name, "passed": False, "reason": "build"})
            continue
        tfm = ET.parse(project).findtext(".//TargetFramework")
        binary = project.parent / "bin/Debug" / tfm
        fixture = fixtures / name
        fixture.mkdir()
        report = output / (name + ".json")
        command = [str(binary / (name + ".exe")), str(fixture), str(report)]
        if name == "WorkspaceChecks":
            command = command[:2]
        elif name == "DialogChecks":
            command = [command[0], str(report)]
        elif name == "FaultChecks":
            command = [command[0], str(fixture), str(binary / "IndieRun.dll"), str(report)]
        elif name == "RecoveryUiChecks":
            gallery = output / "error-gallery"
            gallery.mkdir(exist_ok=True)
            command += [str(gallery), str(project.parent / "error-scenarios.json")]
        elif name == "ExecutableChecks":
            probe = root / "tests/LaunchProbe/LaunchProbe.csproj"
            if not build(probe, output / "LaunchProbe-build.log"):
                results.append({"name": name, "passed": False, "reason": "probe-build"})
                continue
            probe_tfm = ET.parse(probe).findtext(".//TargetFramework")
            command += [str(probe.parent / "bin/Debug" / probe_tfm)]
        log = output / (name + ".log")
        with log.open("w", encoding="utf-8") as stream:
            process = subprocess.Popen(command, cwd=root, stdout=stream, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
            try:
                code = process.wait(timeout=options.timeout)
            except subprocess.TimeoutExpired:
                subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], capture_output=True)
                process.wait()
                code = "timeout"
        data = json.loads(report.read_text(encoding="utf-8-sig")) if report.exists() else {}
        if name == "WorkspaceChecks" and code == 0:
            count = len(re.findall(r"^PASS:", log.read_text(encoding="utf-8", errors="replace"), re.M))
            data = {"total": count, "passed": count, "failed": 0}
        passed = code == 0 and bool(data) and data.get("failed") == 0
        results.append({"name": name, "passed": passed, "exit": code, "result": data})
        print(name + (" PASS" if passed else " FAIL"), flush=True)
        (output / "summary.json").write_text(json.dumps({"fixtures": str(fixtures), "results": results}, indent=2), encoding="utf-8")
    (output / "summary.json").write_text(json.dumps({"fixtures": str(fixtures), "results": results}, indent=2), encoding="utf-8")
    print(output)
    return 0 if all(r["passed"] for r in results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
