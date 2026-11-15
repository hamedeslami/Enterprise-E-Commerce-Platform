namespace Ecommerce.Modules.Identity.Domain;

public enum AuthenticationMethod
{
    Password = 1,
    SmsOtp = 2,
    PasswordTotp = 3,
    PasswordRecoveryCode = 4
}
