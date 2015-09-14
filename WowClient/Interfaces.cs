using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WowClient.Lua.UI;

namespace WowClient
{
    public interface IScreen
    {
        UIObject FocusedWidget { get; }
        UIObject GetWidget(IAbsoluteAddress address);
        T GetWidget<T>(IAbsoluteAddress address) where T : UIObject;
        T GetWidget<T>(string name) where T : UIObject;
        IEnumerable<T> GetWidgets<T>() where T : UIObject;
        IEnumerable<UIObject> GetWidgets();
        IScreen Current { get; }
    }

}
