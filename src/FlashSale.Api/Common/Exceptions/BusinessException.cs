namespace FlashSale.Api.Common.Exceptions;

public class BusinessException : Exception
{
    public BusinessException(string message)
        : base(message)
    {
    }
}
