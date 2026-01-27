require('dotenv').config();

const grpc = require('@grpc/grpc-js');
const protoLoader = require('@grpc/proto-loader');
const child_process = require('child_process');
const { GoogleGenAI, Modality } = require('@google/genai');
const path = require('path');

// =========================================================
// [설정] 모드 선택 (true: 클라우드(구글 제미나이 영어로만 번역) / false: 로컬(내 컴퓨터 GPU 다국어 번역 가능)
// =========================================================
const IS_CLOUD_MODE = false;

// [설정] 경로 및 키
const ffmpegPath = path.join(__dirname, 'ffmpeg.exe');
const GOOGLE_API_KEY = process.env.GEMINI_API_KEY;
const MODEL_NAME = "gemini-2.5-flash-native-audio-preview-12-2025";
const PYTHON_PORT = 'localhost:50051'; // Python 서버 주소
const CSHARP_PORT = 'localhost:5286';  // C# 서버 주소
const PROTO_PATH = './Trans.proto';

// 1. Proto 로드
const packageDefinition = protoLoader.loadSync(PROTO_PATH, {
    keepCase: true, longs: String, enums: String, defaults: true, oneofs: true
});
const lectureProto = grpc.loadPackageDefinition(packageDefinition).lecture;

// 2. C# 서버 연결 (학생들에게 방송 송출용)
const csharpClient = new lectureProto.LectureService(CSHARP_PORT, grpc.credentials.createInsecure());
const csharpStream = csharpClient.StreamLecture();

// C# 서버 상태 로그
csharpStream.on('data', (status) => {}); 
csharpStream.on('end', () => console.log('>>> [C#] 서버 연결 종료'));
csharpStream.on('error', (e) => console.error('>>> [C#] 에러:', e));

// 3. AI 엔진 연결
let geminiSession = null;
let pythonClient = null;
let pythonStream = null;

async function initAI() {
    if (IS_CLOUD_MODE) {
        // [A] 클라우드 모드 (Gemini)
        console.log("(Cloud) 연결 시도...");
        const genAI = new GoogleGenAI({ apiKey: GOOGLE_API_KEY });
        try {
            geminiSession = await genAI.live.connect({
                model: MODEL_NAME,
                config: {
                    responseModalities: [Modality.AUDIO],
                    speechConfig: { voiceConfig: { prebuiltVoiceConfig: { voiceName: "Aoede" } } },
                    systemInstruction: `You are a simultaneous interpreter. Speak only the English translation immediately. Do not explain.`,
                },
                callbacks: {
                    onopen: () => console.log('>>> [Cloud] Gemini 연결 성공!'),
                    onmessage: (msg) => handleGeminiMessage(msg),
                    onerror: (e) => console.error('>>> [Cloud] 에러:', e),
                }
            });
        } catch (e) { console.error("Gemini 연결 실패:", e); }

    } else {
        // [B] 로컬 모드
        console.log(`(Local) 연결 시도... (${PYTHON_PORT})`);
        pythonClient = new lectureProto.AiWorkerService(PYTHON_PORT, grpc.credentials.createInsecure());
        
        // Python으로 보내는 스트림 생성
        pythonStream = pythonClient.ProcessTranslation();

        // Python에서 번역 결과가 오면 -> C# 서버로 토스
        pythonStream.on('data', (msg) => {
            // 자막이나 오디오가 있으면 전송
            if (msg.subtitle || (msg.audio_data && msg.audio_data.length > 0)) {
                
                if (msg.subtitle) {
                    console.log(`[GPU 통역]: ${msg.subtitle}`);
                }
                
                // 오디오 데이터 전달
                csharpStream.write({
                    pcm_data: msg.audio_data || Buffer.alloc(0), // 오디오 있으면 보내고 없으면 빈 것
                    speaker_id: "Instructor_AI",
                    is_cloud_processed: true, 
                    text_data: msg.subtitle || "",
                    language_code: "en-US"
                });
            }
        });
        
        pythonStream.on('end', () => console.log('>>> [Local] Python 연결 종료'));
        pythonStream.on('error', (e) => console.error('>>> [Local] Python 연결 에러 (서버 켜져있나요?):', e));
        console.log('>>> [Local] Python 연결 성공! 방송 준비 완료.');
    }
}

// Gemini 메시지 처리 함수
function handleGeminiMessage(message) {
    let textData = "";
    let audioBuffer = null;
    if (message.serverContent?.modelTurn?.parts) {
        for (const part of message.serverContent.modelTurn.parts) {
            if (part.text) textData += part.text;
            if (part.inlineData) audioBuffer = Buffer.from(part.inlineData.data, 'base64');
        }
    }
    if (textData || audioBuffer) {
        if (textData) console.log(`[Gemini]: ${textData.trim()}`);
        csharpStream.write({
            pcm_data: audioBuffer || Buffer.alloc(0),
            speaker_id: "Instructor_Gemini",
            is_cloud_processed: true,
            text_data: textData,
            language_code: "en-US"
        });
    }
}

// 4. 메인 실행 로직
(async () => {
    await initAI();

    console.log(">>> [시스템] 마이크 장치 검색 중...");
    const listCommand = `"${ffmpegPath}" -list_devices true -f dshow -i dummy`;

    child_process.exec(listCommand, (error, stdout, stderr) => {
        const output = stderr;
        let micName = "";
        //이거 마이크 설정 해줘야 함. 불편한 점임.
        let match = output.match(/"([^"]*A50 X[^"]*)"/) || output.match(/"([^"]*마이크[^"]*)"/) || output.match(/"([^"]*Microphone[^"]*)"/);
        
        if (match) {
            micName = match[1];
            console.log(`>>> [마이크]: ${micName}`);
        } else {
            micName = "마이크(Realtek Audio)"; 
            console.log(`마이크를 못 찾아 기본값(${micName})을 사용합니다.`);
        }

        console.log(`${IS_CLOUD_MODE ? "구글 클라우드" : "내 컴퓨터"}가 통역을 시작합니다.`);

        const ffmpegArgs = [
            '-f', 'dshow', '-i', `audio=${micName}`,
            '-ac', '1', '-ar', '16000', '-f', 's16le', '-'
        ];

        const ffmpegProcess = child_process.spawn(ffmpegPath, ffmpegArgs);

        ffmpegProcess.stdout.on('data', (chunk) => {
            process.stdout.write('.');

            if (IS_CLOUD_MODE) {
                if (geminiSession) {
                    try {
                        geminiSession.sendRealtimeInput({
                            audio: { data: chunk.toString('base64'), mimeType: "audio/pcm;rate=16000" }
                        });
                    } catch (e) {}
                }
            } else {
                if (pythonStream) {
                    try {
                        pythonStream.write({ 
                            pcm_data: chunk,
                            speaker_id: "Instructor",
                            language_code: "ko-KR"
                        });
                    } catch (e) {}
                }
            }
        });
    });
})();