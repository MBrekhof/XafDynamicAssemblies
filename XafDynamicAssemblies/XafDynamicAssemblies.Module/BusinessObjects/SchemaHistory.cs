using System.ComponentModel;
using DevExpress.ExpressApp.DC;
using DevExpress.ExpressApp.Model;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;

namespace XafDynamicAssemblies.Module.BusinessObjects
{
    [DefaultClassOptions]
    [NavigationItem("Schema Management")]
    [DefaultProperty(nameof(Summary))]
    public class SchemaHistory : BaseObject
    {
        public virtual DateTime Timestamp { get; set; }
        public virtual string UserName { get; set; }
        public virtual SchemaChangeAction Action { get; set; }
        public virtual string Summary { get; set; }

        [VisibleInListView(false)]
        [ModelDefault("RowCount", "20")]
        public virtual string Details { get; set; }

        [VisibleInListView(false)]
        [ModelDefault("RowCount", "25")]
        public virtual string SchemaJson { get; set; }
    }

    public enum SchemaChangeAction
    {
        Import,
        Export,
    }
}
