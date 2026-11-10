using Awaiten.AotTests.Domain;
using CoffeeShop;
using CoffeeShop.Composition;

namespace Awaiten.AotTests;

[Container]
[AsyncInitializer<IWarmUp>(nameof(IWarmUp.WarmUpAsync))]
[ImportService<Banner>]
[Singleton<SystemClock, IClock>]
[Transient<Report>]
[Singleton<Warmup>]
[Scan(typeof(IMessageHandler<>), As = ScanAs.Marker)]
[Transient<Mediator>]
[Import(typeof(PaymentModule))]
[Transient<Barista>]
[Transient<Ticket>]
public static partial class SampleContainer;
