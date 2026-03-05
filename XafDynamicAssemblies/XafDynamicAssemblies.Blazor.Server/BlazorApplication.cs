using DevExpress.ExpressApp;
using DevExpress.ExpressApp.ApplicationBuilder;
using DevExpress.ExpressApp.Blazor;
using DevExpress.ExpressApp.EFCore;
using DevExpress.ExpressApp.SystemModule;
using DevExpress.ExpressApp.Updating;
using Microsoft.EntityFrameworkCore;
using XafDynamicAssemblies.Module.BusinessObjects;

namespace XafDynamicAssemblies.Blazor.Server
{
    public class XafDynamicAssembliesBlazorApplication : BlazorApplication
    {
        public XafDynamicAssembliesBlazorApplication()
        {
            ApplicationName = "XafDynamicAssemblies";
            CheckCompatibilityType = DevExpress.ExpressApp.CheckCompatibilityType.DatabaseSchema;
            DatabaseVersionMismatch += XafDynamicAssembliesBlazorApplication_DatabaseVersionMismatch;
        }
        protected override void OnSetupStarted()
        {
            base.OnSetupStarted();

#if DEBUG
            if(System.Diagnostics.Debugger.IsAttached && CheckCompatibilityType == CheckCompatibilityType.DatabaseSchema) {
                DatabaseUpdateMode = DatabaseUpdateMode.UpdateDatabaseAlways;
            }
#endif
        }
        void XafDynamicAssembliesBlazorApplication_DatabaseVersionMismatch(object sender, DatabaseVersionMismatchEventArgs e)
        {
#if EASYTEST
            e.Updater.Update();
            e.Handled = true;
#else
            // Always auto-update: runtime entity changes (including graduation) alter the
            // EF Core model between restarts, which triggers schema mismatch. Refusing to
            // update would crash the restart loop.
            e.Updater.Update();
            e.Handled = true;
#endif
        }
    }
}
