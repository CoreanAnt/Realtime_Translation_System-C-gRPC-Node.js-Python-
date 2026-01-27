using Grpc.Core;
using Microsoft.Extensions.Logging; // 로거 사용을 위해 필요
using Trans.gRPC;

namespace TransServer.Services
{
    public class LectureService : Trans.gRPC.LectureService.LectureServiceBase
    {
        private readonly RoomManager _roomManager;
        private readonly ILogger<LectureService> _logger;

        public LectureService(RoomManager roomManager, ILogger<LectureService> logger)
        {
            _roomManager = roomManager;
            _logger = logger;
        }

        // 1. 학생: 수업 듣기 (변동 없음)
        public override async Task SubscribeClass(StudentRequest request, IServerStreamWriter<BroadcastMessage> responseStream, ServerCallContext context)
        {
            var studentId = request.StudentId;
            _roomManager.JoinStudent(studentId, responseStream);

            try
            {
                while (!context.CancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(1000);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                _roomManager.LeaveStudent(studentId);
            }
        }

        // 2. 강사: 수업 진행 (불필요한 else 제거 버전)
        public override async Task StreamLecture(IAsyncStreamReader<AudioChunk> requestStream, IServerStreamWriter<ServerStatus> responseStream, ServerCallContext context)
        {
            _logger.LogInformation(">>> [강사] 방송 송출 시작");

            try
            {
                await foreach (var request in requestStream.ReadAllAsync())
                {
                    // Node.js가 보내준 '번역된 자막'이 있는 경우에만 처리
                    if (request.IsCloudProcessed && (!string.IsNullOrEmpty(request.TextData) || request.PcmData.Length > 0))
                    {
                        // 1. 서버 콘솔에 로그 출력 (자막이 있을 때만 찍기)
                        if (!string.IsNullOrEmpty(request.TextData))
                        {
                            Console.WriteLine($"💬 [방송 중]: {request.TextData}");
                        }

                        // 2. 학생들에게 방송 송출
                        var broadcastMsg = new BroadcastMessage
                        {
                            LanguageCode = request.LanguageCode,
                            Subtitle = request.TextData ?? "", // 텍스트가 없으면 빈 문자열 안전하게 넣기
                            AudioData = request.PcmData,       // 오디오 데이터 포함
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        };

                        await _roomManager.BroadcastAsync(broadcastMsg);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[오류] 방송 중단: {ex.Message}");
            }
            finally
            {
                _logger.LogInformation("<<< [강사] 방송 송출 종료");
            }
        }
    }
}