namespace Scaffold;

public sealed class DiscontinuedFlatWhite : IDrink;

// A type in another assembly, as far as the page is concerned.
public static class SeasonalDrinks;

public interface IViewModel;
public interface IMenuViewModel;
public sealed class MenuViewModel : IViewModel, IMenuViewModel;

public interface IScreen<TViewModel>
{
	object? ViewModel { get; set; }
}

public sealed class MenuScreen : IScreen<IMenuViewModel>
{
	public object? ViewModel { get; set; }
}
