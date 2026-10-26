using Bibliotaph.Core.Reading;

namespace Bibliotaph.Core.Tests;

public class AddToBinderMenuTests
{
    [Fact]
    public void With_no_binder_yet_the_menu_offers_a_new_one()
    {
        var items = AddToBinderMenu.For([]);

        Assert.Equal([(AddToBinderMenu.Kind.New, (long?)null, "New binder…")], items.Select(i => (i.Kind, i.Id, i.Label)));
    }

    [Fact]
    public void Binders_are_named_in_the_order_given_then_a_new_one()
    {
        var items = AddToBinderMenu.For([(7, "Harbor night"), (3, "The sunken vault")]);

        Assert.Equal(["Harbor night", "The sunken vault", "New binder…"], items.Select(i => i.Label));
        Assert.Equal([7L, 3L, null], items.Select(i => i.Id));
    }

    [Fact]
    public void Past_the_ones_it_names_the_rest_are_behind_another_binder()
    {
        var binders = Enumerable.Range(1, AddToBinderMenu.Named + 2).Select(i => ((long)i, $"Session {i}")).ToList();

        var items = AddToBinderMenu.For(binders);

        Assert.Equal(AddToBinderMenu.Named, items.Count(i => i.Kind == AddToBinderMenu.Kind.Binder));
        Assert.Equal($"Session {AddToBinderMenu.Named}", items[AddToBinderMenu.Named - 1].Label);
        Assert.Equal([AddToBinderMenu.Kind.Another, AddToBinderMenu.Kind.New], items.Skip(AddToBinderMenu.Named).Select(i => i.Kind));
    }
}
