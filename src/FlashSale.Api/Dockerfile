# 多階段建置：建置用 SDK 映像，執行只留 runtime。
# 最終映像不含編譯器與原始碼，體積小、攻擊面也小。

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# 先只複製 csproj 還原套件。
# 這一層只有在相依套件變動時才會失效，改動程式碼時可以直接命中快取。
COPY ["src/FlashSale.Api/FlashSale.Api.csproj", "src/FlashSale.Api/"]
RUN dotnet restore "src/FlashSale.Api/FlashSale.Api.csproj"

COPY src/FlashSale.Api/ src/FlashSale.Api/
RUN dotnet publish "src/FlashSale.Api/FlashSale.Api.csproj" \
    -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# 容器內只走 HTTP，TLS 由 Nginx 終結。
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "FlashSale.Api.dll"]
