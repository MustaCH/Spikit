namespace Spikit.Services.Billing;

// Cliente HTTP de los dos endpoints Stripe-related que expone el backend (ADR-0007 §
// 4.2 y 4.3). Ambos requieren Bearer JWT del user; el backend crea/lee el Stripe
// Customer asociado, abre la session correspondiente, y devuelve la URL para que el
// cliente la abra en el browser.
public interface IStripeBillingClient
{
    // POST /functions/v1/create-checkout-session, body { lookup_key }. Devuelve la URL
    // de Stripe Checkout (hosted) a la que mandar al usuario. `lookupKey` típicamente
    // es "pro_monthly" o "pro_yearly" — el backend filtra cuáles son válidos.
    Task<string> CreateCheckoutSessionAsync(string accessToken, string lookupKey, CancellationToken ct);

    // POST /functions/v1/create-portal-session. Devuelve la URL del Customer Portal
    // (billing.stripe.com) para que el usuario gestione su suscripción. Solo válido si
    // el usuario tiene un Stripe Customer asociado (tier=pro o ex-pro); si no, el
    // backend responde 400.
    Task<string> CreatePortalSessionAsync(string accessToken, CancellationToken ct);
}
