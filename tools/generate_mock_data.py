"""Create harmless game fixtures using only source in this repository."""
from pathlib import Path
import argparse
import json
import os
import shutil
import subprocess
import tempfile


def generate(destination: Path) -> None:
    if destination.exists():
        raise FileExistsError("목데이터는 존재하지 않는 새 폴더에 생성하세요.")
    compiler = Path(os.environ.get("WINDIR", "C:/Windows")) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
    if not compiler.is_file():
        raise FileNotFoundError("목데이터 생성에는 Windows .NET Framework 4.x C# 컴파일러가 필요합니다.")
    source = Path(__file__).with_name("MockGame.cs")
    with tempfile.TemporaryDirectory(prefix="ir-mock-build-") as tmp:
        executable = Path(tmp) / "MockGame.exe"
        subprocess.run([str(compiler), "/nologo", "/codepage:65001", "/target:winexe", "/reference:System.Windows.Forms.dll",
                        "/out:" + str(executable), str(source)], check=True)
        destination.mkdir(parents=True)
        folders = ["QuietGarden", "NightTrain", "GameBundle", "GameBundle/MainQuest", "GameBundle/SideStory"]
        for relative in folders:
            game = destination / relative
            game.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(executable, game / "Game.exe")
            (game / "data.dat").write_text("IndieRun generated fixture data\n", encoding="utf-8")
            (game / "required-data.txt").write_text("MainQuest/data.dat" if relative == "GameBundle" else "data.dat", encoding="utf-8")
        guide = destination / "GameBundle/Guide"
        guide.mkdir()
        (guide / "readme.txt").write_text("이 폴더는 게임 실행에 필요 없는 테스트용 가이드입니다.\n", encoding="utf-8-sig")
        (destination / "readme.txt").write_text("모든 실행 파일과 문서는 저장소 소스로 생성한 예제입니다. 실제 게임이나 개인 정보는 포함하지 않습니다.\n", encoding="utf-8-sig")
        manifest = {"Version": 1, "Games": [
            {"Key": "QuietGarden", "Folder": "QuietGarden", "Alias": "작은 정원", "Executable": "Game.exe", "Added": False},
            {"Key": "NightTrain", "Folder": "NightTrain", "Alias": "밤 기차", "Executable": "Game.exe"},
            {"Key": "GameBundle", "Folder": "GameBundle", "Alias": "게임 묶음", "Executable": "Game.exe", "Added": False},
            {"Key": "MainQuest", "Folder": "GameBundle/MainQuest", "Alias": "메인 퀘스트", "Executable": "Game.exe", "Added": False},
            {"Key": "SideStory", "Folder": "GameBundle/SideStory", "Alias": "사이드 스토리", "Executable": "Game.exe", "Added": False}
        ]}
        (destination / "demo-manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
        for relative in folders:
            subprocess.run([str(destination / relative / "Game.exe"), "--self-test"], check=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path)
    generate(parser.parse_args().destination.resolve())
