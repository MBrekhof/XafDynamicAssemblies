using System.ComponentModel;
using DevExpress.ExpressApp.DC;
using DevExpress.ExpressApp.Model;
using DevExpress.Persistent.Base;

namespace XafDynamicAssemblies.Module.BusinessObjects
{
    [DomainComponent]
    public class SchemaPackage : INotifyPropertyChanged
    {
        private string _schemaJson;

        [VisibleInListView(false)]
        [ModelDefault("RowCount", "25")]
        public string SchemaJson
        {
            get => _schemaJson;
            set
            {
                if (_schemaJson == value) return;
                _schemaJson = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SchemaJson)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
