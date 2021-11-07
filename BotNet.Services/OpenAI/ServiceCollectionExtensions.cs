﻿using Microsoft.Extensions.DependencyInjection;

namespace BotNet.Services.OpenAI {
	public static class ServiceCollectionExtensions {
		public static IServiceCollection AddOpenAIClient(this IServiceCollection services) {
			services.AddTransient<OpenAIClient>();
			services.AddTransient<CodeExplainer>();
			services.AddTransient<AssistantBot>();
			services.AddTransient<Translator>();
			return services;
		}
	}
}
