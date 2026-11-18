# IndieRun

Windows용 인디게임 정리·실행 프로그램입니다. 폴더 구조를 확인해 가져올 범위를 정하고, 현재 위치에서 사용하거나 보관함으로 옮길 수 있습니다. 별칭과 태그로 게임을 찾고 실행합니다.

## 주요 기능

- 여러 탐색 위치에서 실행 파일 후보 검색, 폴더 범위와 구성 확인
- 현재 위치에서 실행 목록 등록, 백업 후 이동 또는 백업 없는 이동
- 게임별 작업 기록, 이동 되돌리기, 별도 위치로 백업 복원
- 카드·목록 보기, 별칭 검색, 태그 AND 필터, 즐겨찾기·숨김
- 별칭·태그·이미지·실행 파일 편집, 다중 선택
- 본편·관련 콘텐츠·별도 게임·제외 폴더 구성
- 식별 파일과 메타데이터를 통한 위치 확인 및 연결 복구

게임의 정체, 버전 차이, 필요한 데이터가 모두 있는지는 자동으로 확정하지 않습니다. 이동 범위를 확인하고 실행·세이브 정상 여부는 직접 확인하세요. 게임 폴더 밖의 세이브나 공유 데이터는 폴더 백업에 포함되지 않습니다. 백업은 사용자가 삭제할 때까지 유지됩니다.

## 개발 실행

Windows x64와 .NET 10 SDK가 필요합니다. SDK 기준은 `global.json`에 명시되어 있습니다. 앱 프로젝트는 외부 NuGet 패키지를 사용하지 않습니다.

```powershell
dotnet build src/IndieRun/IndieRun.csproj
dotnet run --project src/IndieRun/IndieRun.csproj
```

실행 중 생성되는 `data` 폴더는 Git에 포함하지 않습니다. 기존 사용자 데이터로 개발 테스트하지 마세요.

## 테스트

Python 3.10 이상에서 다음 명령을 실행합니다. 개별 테스트 프로젝트는 .NET SDK로 직접 실행할 수도 있습니다.

```powershell
python tools/run_checks.py
python tools/run_checks.py --all
```

기본 명령은 구성·복원·작업 기록 검사를 실행합니다. 테스트는 전용 임시 폴더에 자료를 생성하며, 결과는 `artifacts/checks`에 저장합니다. 일부 테스트는 오류 확인창을 잠시 표시하거나 생성한 자식 프로세스를 강제 종료해 복구를 확인합니다.

## 배포 파일 생성

단일 EXE만 만들려면 다음 명령을 실행합니다. 사용자는 별도 .NET 설치 없이 이 EXE를 사용할 수 있습니다.

```powershell
powershell -ExecutionPolicy Bypass -File tools/Publish-Portable.ps1
```

프로그램 ZIP과 생성 목데이터가 포함된 테스트 묶음은 다음 명령으로 만듭니다.

```powershell
python tools/package_release.py
```

결과는 `artifacts/releases`에 저장합니다. 테스트용 EXE 생성에는 Windows .NET Framework 4.x의 C# 컴파일러가 필요합니다. 모든 예제는 저장소에 포함된 소스에서 생성하며 개인 게임·설정은 사용하지 않습니다.

프로그램을 업데이트할 때는 종료 후 `IndieRun.exe`를 교체하고 기존 `data`와 보관함·백업을 유지하세요.

## 문서

- [사용 방법](docs/USER-GUIDE.md)
- [개발·배포](docs/DEVELOPMENT.md)
- [테스트와 검증 범위](docs/TESTING.md)
- [초기 Git 이력 구성](docs/HISTORY.md)
- [커밋·협업 규칙](CONTRIBUTING.md)

공개 라이선스와 원격 저장소 공개 범위는 아직 정하지 않았습니다.
