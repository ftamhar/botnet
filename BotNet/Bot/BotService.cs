﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BotNet.GrainInterfaces;
using BotNet.Services.BotCommands;
using BotNet.Services.SafeSearch;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Extensions.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;

namespace BotNet.Bot;

public class BotService : IHostedService {
	private readonly TelegramBotClient _botClient;
	private readonly IClusterClient _clusterClient;
	private readonly IServiceProvider _serviceProvider;
	private readonly ILogger<BotService> _logger;
	private readonly TelemetryClient _telemetryClient;
	private User? _me;
	private CancellationTokenSource? _cancellationTokenSource;

	public BotService(
		IClusterClient clusterClient,
		IServiceProvider serviceProvider,
		IOptions<BotOptions> optionsAccessor,
		ILogger<BotService> logger,
		TelemetryClient telemetryClient
	) {
		BotOptions options = optionsAccessor.Value;
		if (options.AccessToken is null) throw new InvalidOperationException("Bot access token is not configured. Please add a .NET secret with key 'BotOptions:AccessToken' or a Docker secret with key 'BotOptions__AccessToken'");
		_botClient = new(options.AccessToken);
		_clusterClient = clusterClient;
		_serviceProvider = serviceProvider;
		_logger = logger;
		_telemetryClient = telemetryClient;
	}

	public async Task StartAsync(CancellationToken cancellationToken) {
		_cancellationTokenSource = new();

		// Initialize services to prevent timeout
		await _serviceProvider.GetRequiredService<SafeSearchDictionary>().EnsureInitializedAsync(_cancellationTokenSource.Token);

		// Get bot identity
		_me = await _botClient.GetMeAsync(cancellationToken);

		// Start the bot
		_botClient.StartReceiving(new DefaultUpdateHandler(HandleUpdateAsync, HandleErrorAsync), cancellationToken: _cancellationTokenSource.Token);
	}

	public Task StopAsync(CancellationToken cancellationToken) {
		_cancellationTokenSource?.Cancel();
		return Task.CompletedTask;
	}

	private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken) {
		try {
			switch (update.Type) {
				case UpdateType.Message:
					_logger.LogInformation("Received message from [{firstName} {lastName}]: '{message}' in chat {chatName}.", update.Message!.From!.FirstName, update.Message.From.LastName, update.Message.Text, update.Message.Chat.Title ?? update.Message.Chat.Id.ToString());
					if (update.Message.Entities?.FirstOrDefault(entity => entity is { Type: MessageEntityType.BotCommand, Offset: 0 }) is { } commandEntity) {
						string command = update.Message.Text!.Substring(commandEntity.Offset, commandEntity.Length);

						// Check if command is in /command@botname format
						int ampersandPos = command.IndexOf('@');
						if (ampersandPos != -1) {
							string targetUsername = command[(ampersandPos + 1)..];

							// Command is not for me
							if (!StringComparer.InvariantCultureIgnoreCase.Equals(targetUsername, _me?.Username)) break;

							// Normalize command
							command = command[..ampersandPos];
						}
						try {
							switch (command.ToLowerInvariant()) {
								case "/flip":
									await FlipFlop.HandleFlipAsync(botClient, update.Message, cancellationToken);
									break;
								case "/flop":
									await FlipFlop.HandleFlopAsync(botClient, update.Message, cancellationToken);
									break;
								case "/ask":
									await Ask.HandleAskAsync(_serviceProvider, botClient, update.Message, cancellationToken);
									break;
							}
						} catch (Exception exc) when (exc is not OperationCanceledException) {
							await Error.HandleErrorAsync(exc, botClient, update.Message, cancellationToken);
							throw;
						}
					}
					break;
				case UpdateType.InlineQuery:
					_logger.LogInformation("Received inline query from [{firstName} {lastName}]: '{query}'.", update.InlineQuery!.From.FirstName, update.InlineQuery.From.LastName, update.InlineQuery.Query);
					if (update.InlineQuery.Query.Trim().ToLowerInvariant() is { Length: > 0 } query) {
						IInlineQueryGrain inlineQueryGrain = _clusterClient.GetGrain<IInlineQueryGrain>($"{query}|{update.InlineQuery.From.Id}");
						using GrainCancellationTokenSource grainCancellationTokenSource = new();
						using CancellationTokenRegistration tokenRegistration = cancellationToken.Register(() => grainCancellationTokenSource.Cancel());
						IEnumerable<InlineQueryResult> inlineQueryResults = await inlineQueryGrain.GetResultsAsync(query, update.InlineQuery.From.Id, grainCancellationTokenSource.Token);
						await botClient.AnswerInlineQueryAsync(
							inlineQueryId: update.InlineQuery.Id,
							results: inlineQueryResults,
							cancellationToken: cancellationToken);
					}
					break;
			}
		} catch (Exception exc) when (exc is not OperationCanceledException) {
			_logger.LogError(exc, "{message}", exc.Message);
			_telemetryClient.TrackException(exc);
		}
	}

	private Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken) {
		string errorMessage = exception switch {
			ApiRequestException apiRequestException => $"Telegram API Error:\n{apiRequestException.ErrorCode}\n{apiRequestException.Message}",
			_ => exception.ToString()
		};
		_logger.LogError(exception, "{message}", errorMessage);
		_telemetryClient.TrackException(exception);
		return Task.CompletedTask;
	}
}
