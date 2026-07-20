using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SDSoftware.RevitTest.Mvvm
{
    /// <summary>
    /// Change notification plus a simple error store, so views can bind validation messages
    /// without the view model knowing anything about WPF.
    /// </summary>
    public abstract class ViewModelBase : INotifyPropertyChanged
    {
        private readonly Dictionary<string, string> _errors = new Dictionary<string, string>();

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>True when no property currently holds a validation error.</summary>
        public bool IsValid => _errors.Count == 0;

        /// <summary>First validation error, or null. Bind this to a message block in the view.</summary>
        public string ErrorMessage
        {
            get
            {
                foreach (var error in _errors.Values)
                {
                    return error;
                }

                return null;
            }
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            Validate();
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>Override to re-run validation whenever a property changes.</summary>
        protected virtual void Validate()
        {
        }

        protected void SetError(string propertyName, string error)
        {
            if (string.IsNullOrEmpty(error))
            {
                _errors.Remove(propertyName);
            }
            else
            {
                _errors[propertyName] = error;
            }

            OnPropertyChanged(nameof(IsValid));
            OnPropertyChanged(nameof(ErrorMessage));
        }

        protected void ClearErrors()
        {
            _errors.Clear();
            OnPropertyChanged(nameof(IsValid));
            OnPropertyChanged(nameof(ErrorMessage));
        }
    }
}
