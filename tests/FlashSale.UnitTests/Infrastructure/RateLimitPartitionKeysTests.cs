using System.Net;
using FlashSale.Api.Infrastructure.RateLimiting;
using Microsoft.AspNetCore.Http;

namespace FlashSale.UnitTests.Infrastructure;

public class RateLimitPartitionKeysTests
{
    private static HttpContext CreateContext(
        string? userId = null,
        string? ip = "203.0.113.10")
    {
        var context = new DefaultHttpContext();

        if (userId is not null)
        {
            context.Request.Headers[RateLimitPartitionKeys.UserHeaderName] = userId;
        }

        context.Connection.RemoteIpAddress =
            ip is null ? null : IPAddress.Parse(ip);

        return context;
    }

    [Fact]
    public void ForUser_WhenHeaderPresent_ShouldPartitionByUser()
    {
        var key = RateLimitPartitionKeys.ForUser(CreateContext(userId: "42"));

        Assert.Equal("user:42", key);
    }

    [Fact]
    public void ForUser_ShouldSeparateDifferentUsers()
    {
        var a = RateLimitPartitionKeys.ForUser(CreateContext(userId: "1"));
        var b = RateLimitPartitionKeys.ForUser(CreateContext(userId: "2"));

        // 分成同一區的話，一個人洗版就會把其他人一起擋住
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ForUser_WhenSameUserFromDifferentIps_ShouldShareOneBudget()
    {
        var a = RateLimitPartitionKeys.ForUser(
            CreateContext(userId: "42", ip: "203.0.113.10"));

        var b = RateLimitPartitionKeys.ForUser(
            CreateContext(userId: "42", ip: "198.51.100.20"));

        // per-User 限制的意義就在這裡：換 IP 不能重置額度
        Assert.Equal(a, b);
    }

    [Fact]
    public void ForUser_WhenHeaderMissing_ShouldFallBackToIp()
    {
        var key = RateLimitPartitionKeys.ForUser(CreateContext(userId: null));

        // 匿名請求若完全不受限，攻擊者只要不帶 Header 就能繞過
        Assert.Equal("ip:203.0.113.10", key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForUser_WhenHeaderIsBlank_ShouldFallBackToIp(string userId)
    {
        var key = RateLimitPartitionKeys.ForUser(CreateContext(userId: userId));

        Assert.Equal("ip:203.0.113.10", key);
    }

    [Fact]
    public void ForUser_ShouldTrimHeaderValue()
    {
        var padded = RateLimitPartitionKeys.ForUser(CreateContext(userId: "  42  "));
        var plain = RateLimitPartitionKeys.ForUser(CreateContext(userId: "42"));

        // 沒有 Trim 的話，多打一個空白就是一份新額度
        Assert.Equal(plain, padded);
    }

    [Fact]
    public void ForUser_ShouldNotCollideWithIpPartition()
    {
        var user = RateLimitPartitionKeys.ForUser(CreateContext(userId: "203.0.113.10"));
        var ip = RateLimitPartitionKeys.ForIp(CreateContext());

        // 使用者 Id 剛好長得像 IP 時不能撞在一起，所以兩者各有前綴
        Assert.NotEqual(user, ip);
    }

    [Fact]
    public void ForIp_WhenRemoteIpUnavailable_ShouldStillReturnAKey()
    {
        var key = RateLimitPartitionKeys.ForIp(CreateContext(ip: null));

        // 回傳 null 會讓限流器把這些請求全部歸到「沒有分區」而不受限
        Assert.Equal("ip:unknown", key);
    }
}
