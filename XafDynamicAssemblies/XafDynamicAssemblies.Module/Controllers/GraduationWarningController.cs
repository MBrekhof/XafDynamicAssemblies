using DevExpress.ExpressApp;
using DevExpress.ExpressApp.ConditionalAppearance;
using DevExpress.ExpressApp.Editors;
using XafDynamicAssemblies.Module.BusinessObjects;

namespace XafDynamicAssemblies.Module.Controllers
{
    /// <summary>
    /// Shows visual warnings for graduated (non-Runtime) entities.
    /// - DetailView: warning message when viewing a graduated entity
    /// - ListView: disables Deploy Schema when graduated entities exist
    /// </summary>
    public class GraduationWarningDetailController : ObjectViewController<DetailView, CustomClass>
    {
        protected override void OnActivated()
        {
            base.OnActivated();
            View.CurrentObjectChanged += (_, _) => ShowWarningIfGraduated();
            ShowWarningIfGraduated();
        }

        private void ShowWarningIfGraduated()
        {
            var cc = View.CurrentObject as CustomClass;
            if (cc == null) return;

            if (cc.Status == CustomClassStatus.Compiled)
            {
                Application.ShowViewStrategy.ShowMessage(
                    $"⚠ '{cc.ClassName}' is graduated (Status = Compiled). " +
                    "It is excluded from runtime compilation. " +
                    "To re-activate it, change Status back to Runtime.",
                    InformationType.Warning);
            }
            else if (cc.Status == CustomClassStatus.Graduating)
            {
                Application.ShowViewStrategy.ShowMessage(
                    $"⚠ '{cc.ClassName}' is in a transitional state (Graduating). " +
                    "Set Status to Runtime or Compiled to resolve.",
                    InformationType.Warning);
            }
        }
    }

    /// <summary>
    /// Shows a warning on the CustomClass ListView when graduated entities exist,
    /// since they won't be included in Deploy Schema.
    /// </summary>
    public class GraduationWarningListController : ObjectViewController<ListView, CustomClass>
    {
        protected override void OnActivated()
        {
            base.OnActivated();
            View.CollectionSource.CollectionChanged += (_, _) => CheckForGraduatedEntities();
        }

        protected override void OnViewControlsCreated()
        {
            base.OnViewControlsCreated();
            CheckForGraduatedEntities();
        }

        private void CheckForGraduatedEntities()
        {
            var graduatedCount = ObjectSpace.GetObjectsQuery<CustomClass>()
                .Count(cc => cc.Status != CustomClassStatus.Runtime);

            if (graduatedCount > 0)
            {
                Application.ShowViewStrategy.ShowMessage(
                    $"⚠ {graduatedCount} entity(ies) have non-Runtime status and will be excluded from Deploy Schema.",
                    InformationType.Warning);
            }
        }
    }
}
