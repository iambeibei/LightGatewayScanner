namespace LightGatewayScanner.Services;

public interface IFeedbackService
{
    void Success();
    void Warning();
    void Error();
    void Tap();
}
