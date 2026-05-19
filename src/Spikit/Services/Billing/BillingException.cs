namespace Spikit.Services.Billing;

// Excepción base para fallas del cliente Stripe. Distinguida de AuthException porque
// la UI las trata distinto: las de auth llevan a re-login, las de billing a "reintentá".
public class BillingException : Exception
{
    public BillingException(string message) : base(message) { }
    public BillingException(string message, Exception inner) : base(message, inner) { }
}
