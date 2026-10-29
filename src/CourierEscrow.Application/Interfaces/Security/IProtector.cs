namespace CourierEscrow.Application.Interfaces;

public interface IProtector
{
    string Protect(string plain);
    string Unprotect(string protectedValue);
}
