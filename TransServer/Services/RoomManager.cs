using Grpc.Core;
using System.Collections.Concurrent;
using Trans.gRPC;

namespace TransServer.Services
{
    // 역할: 접속한 학생들을 관리하고, 강사의 메시지를 학생들에게 브로드캐스팅함
    public class RoomManager
    {
        // Thread-Safe한 딕셔너리로 학생 세션 관리 (Key: StudentID, Value: Stream Writer)
        private readonly ConcurrentDictionary<string, IServerStreamWriter<BroadcastMessage>> _students 
            = new ConcurrentDictionary<string, IServerStreamWriter<BroadcastMessage>>();

        // 학생 입장
        public void JoinStudent(string studentId, IServerStreamWriter<BroadcastMessage> stream)
        {
            _students.TryAdd(studentId, stream);
            Console.WriteLine($"[입장] 학생 {studentId} 접속. (현재 인원: {_students.Count})");
        }

        // 학생 퇴장
        public void LeaveStudent(string studentId)
        {
            _students.TryRemove(studentId, out _);
            Console.WriteLine($"[퇴장] 학생 {studentId} 나감. (현재 인원: {_students.Count})");
        }

        // 현재 접속자 수 반환
        public int GetStudentCount() => _students.Count;

        // [중요] 모든 학생에게 데이터 전송 (Broadcasting)
        public async Task BroadcastAsync(BroadcastMessage message)
        {
            // 접속한 모든 학생에게 병렬로 전송
            foreach (var student in _students)
            {
                try
                {
                    await student.Value.WriteAsync(message);
                }
                catch (Exception ex)
                {
                    // 전송 실패 시 (학생이 강제 종료 등) 리스트에서 제거
                    Console.WriteLine($"[전송 실패] {student.Key}: {ex.Message}");
                    LeaveStudent(student.Key);
                }
            }
        }
    }
}