using Grpc.Core;
using Grpc.Net.Client;
using NAudio.Wave;
using Trans.gRPC;
using System.Text.Json; // JSON 파싱을 위해 필수

// ========================================================
// [설정] 학생이 듣고 싶은 언어 (en, cn, jp, vi, th)
// ========================================================
string TARGET_LANG = "en"; 
// ========================================================

// 1. 오디오 재생 설정 (NAudio)
// Python 서버(FFmpeg)가 24000Hz로 변환해서 보내므로, 여기도 24000이어야 함
var waveFormat = new WaveFormat(24000, 16, 1); 

var bufferedWaveProvider = new BufferedWaveProvider(waveFormat)
{
    // 버퍼가 너무 짧으면 끊기고, 너무 길면 지연됨 (10~20초 권장)
    BufferDuration = TimeSpan.FromSeconds(20), 
    DiscardOnBufferOverflow = true
};

using var waveOut = new WaveOutEvent();
waveOut.Init(bufferedWaveProvider);
waveOut.Play(); // 재생 시작 (데이터가 들어오면 소리가 남)

Console.WriteLine($"[학생] 서버(5286)에 접속 시도 중...");

// 2. gRPC 채널 연결
// (주의: 로컬 테스트가 아니라면 localhost 대신 IP 주소 입력)
using var channel = GrpcChannel.ForAddress("http://localhost:5286");
var client = new LectureService.LectureServiceClient(channel);

// 3. 수업 참여 요청
var request = new StudentRequest { StudentId = "Student_01", TargetLanguage = TARGET_LANG };

try
{
    using var call = client.SubscribeClass(request);
    
    Console.WriteLine($"[학생] 입장 성공! ({TARGET_LANG} 자막 및 오디오 대기 중...)");
    Console.WriteLine("-------------------------------------------------------");

    // 4. 데이터 수신 루프
    await foreach (var response in call.ResponseStream.ReadAllAsync())
    {
        // =========================================================
        // [A] 자막 처리 (JSON 파싱 vs 일반 텍스트)
        // =========================================================
        if (!string.IsNullOrEmpty(response.Subtitle))
        {
            string displaySubtitle = "";
            string rawSubtitle = response.Subtitle.TrimStart();

            // 1. JSON 형식인지 확인 ('{'로 시작하면 JSON으로 간주)
            if (rawSubtitle.StartsWith("{"))
            {
                try
                {
                    // JSON -> Dictionary 변환
                    var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(response.Subtitle);
                    
                    // 내가 원하는 언어(TARGET_LANG)가 있는지 확인
                    if (translations != null && translations.TryGetValue(TARGET_LANG, out string? extractedText))
                    {
                        displaySubtitle = $"[{TARGET_LANG.ToUpper()}] {extractedText}";
                    }
                    else
                    {
                        // 없으면 영어(en)라도 보여줌
                        displaySubtitle = $"[EN] {translations?.GetValueOrDefault("en") ?? "..."}";
                    }
                }
                catch 
                {
                    // 파싱 실패 시 원본 그대로 출력
                    displaySubtitle = $"[Raw] {response.Subtitle}";
                }
            }
            // 2. 일반 텍스트 (Gemini 모드 등)
            else
            {
                displaySubtitle = $"[Gemini] {response.Subtitle}";
            }

            // 화면 출력 (색상 입히기)
            if (!string.IsNullOrWhiteSpace(displaySubtitle))
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine(displaySubtitle);
                Console.ResetColor();
            }
        }

        // =========================================================
        // [B] 오디오 처리 (TTS 재생)
        // =========================================================
        if (response.AudioData.Length > 0)
        {
            byte[] audioBytes = response.AudioData.ToByteArray();

            // [진단 로그] 데이터가 진짜 오는지 눈으로 확인 (노란색)
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write($"[오디오 수신: {audioBytes.Length} bytes] ");
            Console.ResetColor();

            // 버퍼에 오디오 데이터 추가 -> waveOut이 알아서 스피커로 재생함
            bufferedWaveProvider.AddSamples(audioBytes, 0, audioBytes.Length);
        }
    }
}
catch (RpcException rpcEx)
{
    Console.WriteLine($"\n[gRPC 오류] 서버와 연결할 수 없습니다: {rpcEx.Status.Detail}");
}
catch (Exception ex)
{
    Console.WriteLine($"\n[오류] 프로그램 중단: {ex.Message}");
}
finally
{
    waveOut.Stop();
    Console.WriteLine("\n[학생] 종료되었습니다.");
}