# RdpSwitcher

[English README](README.md)

[Code signing policy](CODE_SIGNING_POLICY.md)

RdpSwitcher는 Pause 키를 두 번 눌러 원격 데스크톱 세션과 호스트 데스크톱 사이를 전환하는 작은 Windows 트레이 유틸리티입니다.

호스트 PC와 원격 Windows 세션 양쪽에 같은 프로그램을 설치하고 실행합니다.

- 호스트 PC에서는 RdpSwitcher가 RDP 창을 전환하고, 새 `mstsc.exe` 연결에 RDC Dynamic Virtual Channel AddIn을 활성화합니다.
- 원격 세션에서는 RdpSwitcher가 Pause x2 입력을 감지하고 `RDPSWCH` Dynamic Virtual Channel로 신호를 보냅니다.

RdpSwitcher는 파일 기반 신호나 RDP 드라이브 리디렉션을 사용하지 않습니다. 네이티브 `RdpSwitcher.Plugin.dll`은 `mstsc.exe`에 로드되어 `RDPSWCH` Dynamic Virtual Channel을 수신하고, 받은 메시지를 로컬 named pipe `RdpSwitcher.Signal`을 통해 트레이 앱으로 전달합니다.

호스트 PC에서 실행될 때 RdpSwitcher는 새 `mstsc.exe` 연결이 RDC Dynamic Virtual Channel 플러그인을 로드하도록 다음 레지스트리 값을 씁니다.

```text
HKCU\Software\Microsoft\Terminal Server Client\Default\AddIns\RdpSwitcher
Name = {8A1E8AC0-827E-42F8-8B65-8D65C7A6AB7D}
```

정상 종료 시 RdpSwitcher는 이 RDC AddIn 키만 제거합니다. `RdpSwitcher.Plugin.dll`의 COM 등록은 의도적으로 남겨둡니다.

호스트 시작 시 RdpSwitcher는 `RdpSwitcher.exe` 옆의 `RdpSwitcher.Plugin.dll`이 현재 사용자 COM in-process server로 등록되어 있는지도 확인합니다. 없거나 다른 경로를 가리키면 다음 값을 씁니다.

```text
HKCU\Software\Classes\CLSID\{8A1E8AC0-827E-42F8-8B65-8D65C7A6AB7D}
  (Default) = RdpSwitcher Dynamic Virtual Channel Plugin

HKCU\Software\Classes\CLSID\{8A1E8AC0-827E-42F8-8B65-8D65C7A6AB7D}\InprocServer32
  (Default) = <RdpSwitcher 설치 폴더>\RdpSwitcher.Plugin.dll
  ThreadingModel = Both
```

이 현재 사용자 COM 등록은 관리자 권한이 필요하지 않습니다. RdpSwitcher는 종료 시 COM 등록을 해제하지 않고, RDC AddIn 키만 제거합니다.

보호된 설치 위치 검사는 `ComPluginRegistration`의 `RequireTrustedInstallLocation` 값으로 제어합니다. Debug 빌드에서는 로컬 개발을 위해 `false`이고, `Program Files`가 아닌 경로에서도 COM 등록을 허용합니다. Release 빌드에서는 `true`이며, `Program Files` 또는 `Program Files (x86)` 아래에서만 COM 등록을 허용합니다.

자동 등록은 다음 조건을 모두 만족할 때만 실행됩니다.

- RdpSwitcher가 원격 세션 안이 아니라 호스트 쪽에서 실행 중입니다.
- `RdpSwitcher.Plugin.dll`이 `RdpSwitcher.exe` 옆에 있습니다.
- `RequireTrustedInstallLocation`이 `true`이면 설치 폴더가 `Program Files` 또는 `Program Files (x86)` 아래에 있습니다.

현재 등록 판단 결과는 시작 시 날짜별 로그 파일에 기록됩니다.

```text
%LOCALAPPDATA%\RdpSwitcher\RdpSwitcher-yyyy-MM-dd.log
```

## 사용법

1. RdpSwitcher를 빌드하거나 설치해서 다음 두 파일이 같은 폴더에 있게 합니다.

```text
RdpSwitcher.exe
RdpSwitcher.Plugin.dll
```

2. 새 RDP 연결을 열기 전에 호스트 PC에서 `RdpSwitcher.exe`를 먼저 실행합니다.

호스트 시작 시 앱은 필요한 경우 현재 사용자 COM 플러그인을 등록하고 RDC AddIn 레지스트리 키를 활성화합니다. 이 AddIn은 새 `mstsc.exe` 연결에 적용되므로, 이미 열려 있던 RDP 창은 RdpSwitcher 실행 후 닫았다가 다시 열어야 합니다.

3. Remote Desktop으로 원격 Windows 세션에 접속합니다.

4. 원격 세션 안에서도 `RdpSwitcher.exe`를 실행합니다.

호스트 인스턴스는 `Host listener`로 동작합니다. 원격 인스턴스는 `Remote sender`로 동작합니다. 현재 역할은 트레이 아이콘 메뉴에서 확인할 수 있습니다.

5. 원격 세션 안에서 `Pause` 키를 두 번 누릅니다.

원격 앱이 `RDPSWCH` 채널로 DVC 메시지를 보냅니다. 호스트 쪽 `mstsc.exe` 플러그인이 이 메시지를 `RdpSwitcher.Signal` named pipe로 호스트 트레이 앱에 전달하고, 호스트 트레이 앱이 활성 RDP 창을 전환합니다.

RDP 창이 여러 개 있을 때 전환 순서는 다음과 같습니다.

```text
RDP 1 -> RDP 2 -> 호스트 데스크톱 -> RDP 1
```

6. 새 RDP 연결에서 플러그인을 더 이상 로드하지 않으려면 호스트 트레이 앱을 종료합니다.

정상 종료 시 RdpSwitcher는 RDC AddIn 키만 제거합니다. COM CLSID 등록은 유지됩니다. 이미 실행 중인 `mstsc.exe` 프로세스는 해당 RDP 창이 닫힐 때까지 플러그인을 계속 로드하고 있을 수 있습니다.

로그는 날짜별로 생성됩니다.

```text
%LOCALAPPDATA%\RdpSwitcher\RdpSwitcher-yyyy-MM-dd.log
```

전환이 동작하지 않으면 로그에서 다음 항목을 확인하세요.

- `COM plug-in registration check`
- `Named pipe server starting`
- `Sent DVC signal`
- `Received DVC signal from plug-in`

## 빌드

먼저 네이티브 RDC 플러그인을 빌드한 뒤 트레이 앱을 publish합니다. 그래야 `RdpSwitcher.Plugin.dll`이 `RdpSwitcher.exe` 옆에 복사됩니다.

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" `
  RdpSwitcher.Plugin\RdpSwitcher.Plugin.vcxproj `
  /p:Configuration=Release `
  /p:Platform=x64 `
  /m

dotnet publish RdpSwitcher.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  /p:PublishSingleFile=true
```

## 릴리스

릴리스 태그가 push되면 GitHub Actions가 MSI를 빌드하고 GitHub Release에 업로드합니다.

릴리스 워크플로는 WiX Toolset v7을 사용하며 MSI 빌드 시 `-acceptEula wix7`을 전달합니다. 현재 사용 목적에서 WiX v7 OSMF EULA를 수락할 수 있는지 확인하세요.

세 부분으로 된 숫자 태그를 사용합니다.

```powershell
./build/Push-ReleaseTag.ps1
```

워크플로는 self-contained `win-x64` 빌드를 만들고, `RdpSwitcher-v1.0.0-win-x64.msi`로 패키징한 뒤 해당 태그의 GitHub Release에 첨부합니다.

태그를 직접 넘길 수도 있습니다.

```powershell
./build/Push-ReleaseTag.ps1 -Tag v1.0.0
```

가장 최근 로컬 태그와 같은 이름의 원격 태그를 삭제하려면 다음 스크립트를 사용합니다.

```powershell
./build/Remove-LatestReleaseTag.ps1
```

확인 프롬프트를 건너뛰려면 `-Force`를 사용합니다.

## 라이선스

MIT
