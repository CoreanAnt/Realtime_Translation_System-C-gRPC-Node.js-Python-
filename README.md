실시간 AI 번역 시스템 (Real-time AI Translation System)

본 프로젝트는 강연자의 음성을 실시간으로 수집하여 AI(LLM)를 통해 다국어로 번역한 뒤, 여러 명의 수강생에게 저지연(Low-Latency)으로 스트리밍하는 gRPC 기반 분산 시스템입니다.
바이브코딩으로 만든 프로젝트입니다.

(추후에 오디오도 클라이언트 측에서 조작 가능하도록 수정 예정)







Tech Stack

Main Server: C# / .NET 9 (gRPC),전체 시스템 제어 및 메시지 중계 (Hub)

Instructor (Client): Node.js,"음성 캡처, Gemini AI 기반 음성 분석 및 전송"

Translation Worker: Python 3.11,"STT(SenseVoice), LLM(Qwen), TTS(Edge-TTS) 처리"

Student (Client): C# Console, 실시간 번역 오디오 수신 및 재생



시스템 아키텍처 및 실행 순서

1\.TransServer (C#): gRPC 서버 기동 및 통신 채널 대기

2\.TransWorker (Python): AI 엔진 및 통신 서버 활성화 (CUDA 가속 사용)

3\.TransInstructor (Node.js): 강연자 음성 입력 및 전송 시작

4\.TransStudent (C#): 실시간 번역 결과 수신 및 오디오 출력







전제 조건

FFmpeg: TransInstructor,TransWorker 폴더 내에 ffmpeg.exe 배치가 필요합니다.

GPU: CUDA 13.0 호환 환경 (12도 가능 한 것으로 알고는 있음)



\[Server \& Student (C#)]

\# 서버 빌드

cd TransServer

dotnet build



\# 학생용 콘솔 실행 준비

cd ../TransStudent

dotnet restore



\[Instructor (Node.js)]

cd TransInstructor

npm install

\# .env 파일에 GEMINI\_API\_KEY 설정 필요



\[Worker (Python)]

Bash

cd TransWorker

py -3.11 -m venv .venv

.\\.venv\\Scripts\\activate

pip install torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cu130

pip install funasr modelscope transformers accelerate qwen\_vl\_utils grpcio grpcio-tools protobuf soundfile numpy edge-tts

\# gRPC 프로토콜 컴파일

python -m grpc\_tools.protoc -I. --python\_out=. --grpc\_python\_out=. Trans.proto

아래는 시연영상입니다.


https://github.com/user-attachments/assets/3dc69e5e-a377-4bac-b15f-8562feb76f3c


