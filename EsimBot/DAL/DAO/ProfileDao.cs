namespace EsimBot.DAL.DAO;

/// <summary>Stored eSIM profile: activation data is authenticated ciphertext, not the delivery DTO.</summary>
public sealed record ProfileDao(
    string OrderId,
    string ProviderEsimId,
    string Iccid,
    string ActivationCiphertext,
    string? Apn);
