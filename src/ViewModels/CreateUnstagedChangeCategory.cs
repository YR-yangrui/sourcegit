using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class CreateUnstagedChangeCategory : Popup
    {
        [Required(ErrorMessage = "Category name is required!")]
        [CustomValidation(typeof(CreateUnstagedChangeCategory), nameof(ValidateName))]
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value, true);
        }

        public CreateUnstagedChangeCategory(Repository repo, WorkingCopy workingCopy, List<Models.Change> changes)
        {
            _repo = repo;
            _workingCopy = workingCopy;
            _changes = changes;
        }

        public static ValidationResult ValidateName(string name, ValidationContext context)
        {
            if (context.ObjectInstance is not CreateUnstagedChangeCategory popup || string.IsNullOrWhiteSpace(name))
                return ValidationResult.Success;

            var trimmedName = name.Trim();
            var categories = popup._repo.UIStates.UnstagedChangeCategories;
            if (categories != null)
            {
                // Category names identify persisted path lists, so duplicate names would make moves ambiguous.
                foreach (var category in categories)
                {
                    if (category.Name.Equals(trimmedName, StringComparison.Ordinal))
                        return new ValidationResult("A category with the same name already exists!");
                }
            }

            return ValidationResult.Success;
        }

        public override Task<bool> Sure()
        {
            _workingCopy.CreateUnstagedChangeCategory(_name, _changes);
            return Task.FromResult(true);
        }

        private readonly Repository _repo;
        private readonly WorkingCopy _workingCopy;
        private readonly List<Models.Change> _changes;
        private string _name = string.Empty;
    }
}
