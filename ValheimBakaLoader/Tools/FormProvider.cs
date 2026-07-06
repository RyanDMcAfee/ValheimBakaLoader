using Microsoft.Extensions.DependencyInjection;
using System;
using System.Windows.Forms;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// Indirection for creating forms through the DI container, so one form
    /// can summon another without holding an IServiceProvider itself.
    /// </summary>
    public interface IFormProvider
    {
        T GetForm<T>() where T : Form;
    }

    public class FormProvider : IFormProvider
    {
        private readonly IServiceProvider Services;

        public FormProvider(IServiceProvider services)
        {
            Services = services;
        }

        public T GetForm<T>() where T : Form => Services.GetRequiredService<T>();
    }
}
