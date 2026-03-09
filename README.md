# FileX — LAN File Transfer (Desktop)

WPF 기반 Windows 데스크탑 앱으로, 같은 LAN에 있는 PC 또는 모바일 기기 간에 파일을 주고받을 수 있습니다.

## 기술 스택

| 항목 | 내용 |
|------|------|
| Framework | .NET 9.0 (net9.0-windows) |
| UI | WPF (Windows Presentation Foundation) |
| 웹 서버 | ASP.NET Core Minimal API (Kestrel, 내장) |
| 피어 탐색 | TCP 서브넷 스캔 + UDP 멀티캐스트/브로드캐스트 |
| 통신 프로토콜 | HTTP REST API |
| 테마 | 다크 테마 (퍼플 액센트 #6c6cf0) |

## 주요 기능

- **양방향 파일 탐색** — 로컬(왼쪽 패널)과 원격(오른쪽 패널) 파일 시스템 동시 탐색
- **파일/폴더 전송** — 드래그 앤 드롭 또는 Send/Receive 버튼으로 업로드/다운로드
- **자동 피어 탐색** — TCP 서브넷 스캔(30초 간격) + UDP 멀티캐스트(10초 간격)
- **수동 연결** — IP 직접 입력으로 피어 연결 (`192.168.1.5` 또는 `192.168.1.5:5000`)
- **연결 끊기** — 선택된 피어 연결 해제 (✕ 버튼)
- **전송 취소** — 파일 전송 중 Cancel 버튼으로 즉시 취소 (CancellationToken 기반)
- **연결 끊김 자동 감지** — 10초 간격 health check로 응답 없는 피어 자동 제거
- **전송 진행률** — 파일별 실시간 진행률 표시, 완료 후 3초 후 자동 제거
- **마우스 마키(rubber-band) 선택** — 빈 공간에서 드래그하여 여러 파일 선택
- **로컬 폴더 실시간 감시** — FileSystemWatcher로 외부 변경사항 자동 반영
- **원격 파일 삭제** — Remote 패널에서 파일/폴더 삭제
- **방화벽 자동 설정** — 최초 실행 시 Windows 방화벽 규칙 자동 등록 (UAC)

## 프로젝트 구조

```
FileX/
├── App.xaml                  # 리소스(색상, 스타일, 테마) 정의
├── App.xaml.cs               # Kestrel 웹 서버, 피어 탐색, 방화벽 설정
├── MainWindow.xaml           # 메인 UI 레이아웃 (2패널 파일 브라우저)
├── MainWindow.xaml.cs        # UI 로직, 피어 관리, 전송, 드래그&드롭
├── appsettings.json          # 포트, 탐색 설정
│
├── Models/
│   ├── FileSystemEntry.cs    # 파일/폴더 메타데이터
│   ├── DriveEntry.cs         # 드라이브 정보
│   ├── PeerInfo.cs           # 피어 연결 정보 (Id, Address, MachineName, LastSeen, IsManual)
│   ├── TransferItem.cs       # 전송 대기열 항목
│   └── TransferProgressInfo.cs # 전송 진행 상태 (INotifyPropertyChanged)
│
├── Services/
│   ├── FileSystemService.cs     # 로컬 파일 시스템 작업
│   ├── PeerDiscoveryService.cs  # 피어 자동 탐색 (TCP 스캔 + UDP)
│   ├── PeerApiClient.cs         # 원격 피어 HTTP API 클라이언트
│   └── ProgressStream.cs        # 스트림 전송 진행률 추적
│
└── Controls/
    └── MarqueeAdorner.cs     # 마키 선택 시각적 오버레이
```

## REST API 엔드포인트

내장 Kestrel 서버가 아래 API를 제공합니다 (기본 포트: 5000):

| 메서드 | 경로 | 설명 |
|--------|------|------|
| GET | `/api/peer/info` | 머신 이름 반환 |
| GET | `/api/peer/drives` | 드라이브 목록 |
| GET | `/api/peer/directory?path=` | 디렉토리 내용 |
| GET | `/api/peer/file?path=` | 파일 다운로드 |
| POST | `/api/peer/file?path=` | 파일 업로드 |
| POST | `/api/peer/mkdir?path=` | 디렉토리 생성 |
| DELETE | `/api/peer/file?path=` | 파일/폴더 삭제 |
| POST | `/api/peer/connect` | 양방향 피어 등록 |

## 설정 (appsettings.json)

```json
{
  "FileX": {
    "Port": 5000,
    "DiscoveryPort": 5001,
    "DiscoveryIntervalSeconds": 10,
    "PeerTimeoutSeconds": 30
  }
}
```

| 설정 | 기본값 | 설명 |
|------|--------|------|
| Port | 5000 | HTTP 서버 포트 |
| DiscoveryPort | 5001 | UDP 탐색 포트 |
| DiscoveryIntervalSeconds | 10 | UDP 탐색 주기(초) |
| PeerTimeoutSeconds | 30 | 피어 타임아웃(초) |

## 피어 탐색 방식

1. **TCP 서브넷 스캔** (주요) — 로컬 서브넷의 모든 IP를 30초 간격으로 스캔, 동시 30개 연결, 800ms 타임아웃
2. **UDP 멀티캐스트** (보조) — `239.255.45.88:5001` 그룹으로 10초 간격 브로드캐스트
3. **수동 연결** — 사용자가 IP를 직접 입력하여 연결 (`IsManual=true`)

## 빌드 및 실행

```bash
# 빌드
dotnet build --configuration Release

# 실행
dotnet run
```

**출력 경로:** `bin/Release/net9.0-windows/FileX.exe`

## 시스템 요구사항

- Windows 10 이상
- .NET 9.0 Runtime
- 같은 LAN 네트워크에 연결된 상태
