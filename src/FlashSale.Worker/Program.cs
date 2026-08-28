using FlashSale.Api.Extensions;
using FlashSale.Worker;
using FlashSale.Worker.Options;

// ContentRootPath 必須指向組件所在目錄，而不是預設的「目前工作目錄」。
//
// Worker 的 appsettings 是從 FlashSale.Api 連結過來的（見 .csproj），
// 只會出現在建置輸出目錄。用預設的 content root 時，
// dotnet run 會把專案目錄當成根目錄，那裡沒有這些檔案 ——
// 於是所有設定都是空的，RabbitMq:HostName 變成空字串，
// 連線就會指向一個莫名其妙的位址。
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Services.Configure<WorkerOptions>(
    builder.Configuration.GetSection(WorkerOptions.SectionName));

// 重用 API 的依賴註冊：DbContext、Repository、Redis 連線、RabbitMQ 連線。
// Worker 需要的正是資料存取與訊息傳輸，那些定義已經在那裡了。
//
// 設定檔也是共用的（見 .csproj 的 Link 設定）：連線字串、RabbitMQ 端點、
// 佇列名稱只要兩邊有一點不一致，就會出現「訊息發出去了但沒人收」
// 這種最難查的問題。
builder.Services.AddApplicationDependencies(builder.Configuration);

builder.Services.AddHostedService<OrderCreatedConsumer>();

var host = builder.Build();

await host.RunAsync();
