using AutoMapper;
using FlashSale.Api.Mappings;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlashSale.UnitTests.Mappings;

public class MappingConfigurationTests
{
    [Fact]
    public void Configuration_ShouldBeValid()
    {
        var configuration = new MapperConfiguration(
            cfg => cfg.AddMaps(typeof(ProductProfile).Assembly),
            NullLoggerFactory.Instance);

        configuration.AssertConfigurationIsValid();
    }
}
