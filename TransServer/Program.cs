using TransServer.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddGrpc();

// [중요] RoomManager를 싱글톤(앱 실행 중 1개만 존재)으로 등록
builder.Services.AddSingleton<RoomManager>();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.MapGrpcService<LectureService>();

app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client.");

app.Run();