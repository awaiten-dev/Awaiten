using Awaiten;

namespace CoffeeShop.Composition;

/// <summary>
///     The domain's own composition fragment: it registers the internal <see cref="CardTerminal" /> so that a
///     container in another assembly can use it without the domain making it public. This namespace is the only
///     place in the domain assembly that references Awaiten.
/// </summary>
[Module]
[Singleton<CardTerminal, IPaymentGateway>]
public static partial class PaymentModule;
