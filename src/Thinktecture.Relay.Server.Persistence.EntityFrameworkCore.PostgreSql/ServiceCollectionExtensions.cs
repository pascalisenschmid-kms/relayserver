using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Thinktecture.Relay.Server.Persistence.EntityFrameworkCore.DbContexts;

namespace Thinktecture.Relay.Server.Persistence.EntityFrameworkCore.PostgreSql
{
	/// <summary>
	/// Provides extensions methods for the <see cref="IServiceCollection"/>.
	/// </summary>
	public static class ServiceCollectionExtensions
	{
		/// <summary>
		/// Registers the <see cref="RelayDbContext"/> as a service in the <see cref="IServiceCollection"/>.
		/// </summary>
		/// <param name="serviceCollection">The <see cref="IServiceCollection" /> to add services to.</param>
		/// <param name="connectionString">The connection string to use.</param>
		/// <param name="optionsAction">An optional action to allow additional Npgsql-specific configuration.</param>
		/// <param name="addDefaultMigrations">Adds the default migrations if true.</param>
		/// <param name="contextLifetime">The lifetime with which to register the DbContext service in the container.</param>
		/// <param name="optionsLifetime">The lifetime with which to register the DbContextOptions service in the container.</param>
		/// <returns>The same service collection so that multiple calls can be chained.</returns>
		public static IServiceCollection AddRelayServerConfigurationDbContext(this IServiceCollection serviceCollection,
			string connectionString,
			Action<NpgsqlDbContextOptionsBuilder> optionsAction = null,
			bool addDefaultMigrations = true,
			ServiceLifetime contextLifetime = ServiceLifetime.Scoped,
			ServiceLifetime optionsLifetime = ServiceLifetime.Scoped)
		{
			return serviceCollection.AddDbContext<RelayDbContext>(contextOptionsBuilder =>
					{
						contextOptionsBuilder.UseNpgsql(connectionString, optionsBuilder =>
						{
							if (addDefaultMigrations)
							{
								optionsBuilder.MigrationsAssembly(typeof(ServiceCollectionExtensions).Assembly.GetName().Name);
							}

							optionsAction?.Invoke(optionsBuilder);
						});
					},
					contextLifetime,
					optionsLifetime
				)
				.AddRelayServerEntityFrameworkCoreRepositories();
		}
	}
}
