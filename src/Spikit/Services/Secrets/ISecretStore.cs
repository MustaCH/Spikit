namespace Spikit.Services.Secrets;

// Almacén de secretos por clave canónica (ej. "provider.apiKey"). RN-3 obliga a que
// las credenciales del usuario se guarden cifradas con DPAPI; esta interfaz abstrae
// el mecanismo para tests + para una eventual rotación a otro mecanismo (Pro license,
// otros providers).
public interface ISecretStore
{
    // null si la key no existe o no se pudo descifrar (ej. CB-14: usuario distinto al
    // que cifró). El caller decide si tratarlo como ausencia o como error fatal.
    string? Read(string key);

    // Cifra y persiste. Sobrescribe si ya existe. Tira IOException o
    // CryptographicException si el filesystem o DPAPI rechazan.
    void Write(string key, string value);

    // Idempotente: no tira si la key no existe.
    void Delete(string key);
}
