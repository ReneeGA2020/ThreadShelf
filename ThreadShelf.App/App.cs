using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;

using static Microsoft.UI.Reactor.Factories;

internal sealed class App : Component
{
    public override Element Render() => Component<ThreadShelfController>();
}
