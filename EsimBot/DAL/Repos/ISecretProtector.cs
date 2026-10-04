namespace EsimBot.DAL.Repos;

public interface ISecretProtector
{
    string Encrypt(string orderId, string activation);
    string Decrypt(string orderId, string encrypted);
}
