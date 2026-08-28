using FlashSale.Api.Extensions;
using FlashSale.Api.Middlewares;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers(options =>
{
    // Action 一律以 Async 結尾（Guideline §21）。
    // 保留後綴，nameof(XxxAsync) 才能正確產生路由。
    options.SuppressAsyncSuffixInActionNames = false;
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddApplicationDependencies(
    builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<GlobalExceptionMiddleware>();

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
