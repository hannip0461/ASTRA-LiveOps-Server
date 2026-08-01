# 정적 분석 기준

기본 빌드는 컴파일러 경고를 오류로 처리한다. `AnalysisMode=Recommended`는 별도 품질 점검으로 실행하며 현재 기본 게이트에는 포함하지 않는다.

## 측정 결과

- 최초 측정: 2026-08-01, 167건
- 현재 기준: 2026-08-02, 163건
- 환경: .NET SDK 10.0.301, Release 빌드
- 결과: 명명, 성능, 세계화 권고 163건

| 규칙 | 건수 | 판정 |
|---|---:|---|
| CA1707 | 102 | xUnit 테스트 이름의 밑줄 |
| CA1848 | 27 | `LoggerMessage` 사용 권고 |
| CA1873 | 11 | 단순 필드인 로그 인수 평가 비용 |
| CA1305 | 7 | 테스트 중심의 `IFormatProvider` 권고 |
| CA1711 | 4 | xUnit 컬렉션과 이벤트 처리기 이름 |
| CA1869 | 4 | 테스트의 `JsonSerializerOptions` 캐싱 |
| CA1859 | 3 | 구체 타입 반환 권고 |
| CA1826 | 2 | 인덱서 사용 권고 |
| CA1716 | 2 | 다른 언어 예약어와 겹치는 이름 |
| CA1865 | 1 | `StartsWith(char)` 사용 권고 |

최초 측정 이후 측정용 출력 코드를 줄이면서 CA1305 4건이 제거됐다. 나머지는 대부분 테스트 명명 규칙과 미세 최적화 권고다. 규칙을 일괄 적용하면 테스트 가독성과 기존 명명 규칙을 해치므로 현재 설정을 유지한다.

## 재검사

```powershell
dotnet build Astra.LiveOps.slnx -c Release -p:AnalysisMode=Recommended -p:TreatWarningsAsErrors=false
```

건수나 규칙 구성이 달라지면 원인을 확인하고 이 문서를 갱신한다.
