# 개발·배포

## 구성

- `src/IndieRun`: .NET 10 WPF 앱과 파일·메타데이터 처리
- `tests`: 독립 실행형 검증 프로젝트와 생성용 보조 프로그램
- `tools`: 테스트 실행, 단일 EXE 게시, 목데이터·ZIP 생성
- `docs`: 현재 사용법과 개발·검증 안내

화면은 WPF의 MainWindow partial 파일로 나뉘어 있습니다. 테마 역할 색상은 `Themes/Colors.xaml`에 정의합니다. 파일 작업은 백업·이동·구성·식별 및 저장 모듈에서 처리하고 관련 테스트를 함께 유지합니다.

## 개발 환경

Windows x64, .NET 10 SDK가 필요합니다. `global.json`은 검증에 사용한 10.0.402를 기준으로 같은 메이저 버전의 이후 기능 SDK로 이동할 수 있게 설정합니다. Python 도구는 3.10 이상과 표준 라이브러리만 사용합니다.

```powershell
dotnet build src/IndieRun/IndieRun.csproj
python tools/run_checks.py CompositionChecks RestoreFlowChecks
```

새 사용자 환경에서 원격 NuGet 조회가 실패하는 경우 오류와 경고를 구분해 확인하세요. 이 앱은 외부 NuGet 패키지를 참조하지 않지만 SDK와 .NET 런타임 게시용 자산은 필요합니다.

## 단일 EXE

```powershell
powershell -ExecutionPolicy Bypass -File tools/Publish-Portable.ps1 -OutputDirectory .\artifacts\portable-check
```

출력은 새 폴더여야 합니다. 프로필은 `win-x64`, self-contained, 단일 EXE로 게시합니다. 앱 실행 전에는 출력 파일이 `IndieRun.exe` 하나인지 확인합니다.

## 테스트 묶음

```powershell
python tools/package_release.py --output .\artifacts\release-check
```

출력에는 프로그램 ZIP, TestKit ZIP, MockData ZIP 및 SHA-256 목록이 포함됩니다. ZIP에 넣는 파일은 새로 빌드한 EXE와 생성한 목데이터·안내문으로 제한합니다. 기존 게임 폴더나 사용자 설정을 읽어 패키징하지 않습니다. 출력 폴더가 이미 있으면 중단합니다.

`generate_mock_data.py`는 Windows의 .NET Framework 4.x C# 컴파일러로 `MockGame.cs`를 컴파일합니다. 예제 게임은 안내창만 표시하고 필요한 예제 데이터의 존재를 확인합니다. `GameBundle`의 상위 EXE는 `MainQuest` 하위 자료를 필요로 하므로 하위 폴더 누락 테스트에 사용할 수 있습니다. Guide는 비필수 자료입니다.

```powershell
python tools/generate_mock_data.py .\artifacts\mock-check
```

TestKit을 새 폴더에 풀고 `StartDemo.cmd`로 실행합니다. 테스트 예제의 실행 파일은 .NET Framework 4.x를 사용하며 실제 게임이나 제3자 이미지·문서를 포함하지 않습니다.

## Git에 포함하지 않는 파일

개인 게임, 백업, 실행 중 생성한 `data`, 로컬 검수 보고서, 테스트 결과·캡처, EXE·DLL·ZIP, 빌드 산출물은 제외합니다. 예전 로컬 문서도 승인한 현재 문서 목록에 없는 것은 제외합니다. 새 문서를 추가할 때는 `.gitignore`의 문서 허용 목록을 함께 검토하세요.

커밋 후에도 실제 사용 중인 프로그램 데이터는 별도로 남습니다. 개발 소스를 업데이트하기 위해 사용자 프로그램을 강제 종료하거나 실행 데이터를 지울 필요는 없습니다.
