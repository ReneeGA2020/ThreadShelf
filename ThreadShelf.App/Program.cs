using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;

ReactorApp.Run(_ => ReactorApp.OpenWindow(
        new WindowSpec
        {
            Title = "ThreadShelf",
            Width = 1240,
            Height = 800,
            MinWidth = 980,
            MinHeight = 560
        },
        () => new App()));
