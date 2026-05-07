namespace Spikit.Services.Onboarding;

// Encapsula la persistencia del flag "onboardingCompleted" en JsonSettings. Inyectada en
// el OnboardingViewModel para que pueda marcar el flag al apretar Finalizar/Saltar sin
// acoplarse al AppSettings completo, y para que sea fácil mockear en tests del VM.
public interface IOnboardingCompletionStore
{
    bool IsCompleted();
    void MarkCompleted();
}
