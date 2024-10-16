﻿using Bit.Core.AdminConsole.Entities;
using Bit.Core.AdminConsole.Entities.Provider;
using Bit.Core.AdminConsole.Enums.Provider;
using Bit.Core.AdminConsole.Repositories;
using Bit.Core.Billing.Constants;
using Bit.Core.Billing.Entities;
using Bit.Core.Billing.Enums;
using Bit.Core.Billing.Migration.Models;
using Bit.Core.Billing.Repositories;
using Bit.Core.Billing.Services;
using Bit.Core.Repositories;
using Bit.Core.Services;
using Microsoft.Extensions.Logging;
using Stripe;

namespace Bit.Core.Billing.Migration.Services.Implementations;

public class ProviderMigrator(
    IClientOrganizationMigrationRecordRepository clientOrganizationMigrationRecordRepository,
    IOrganizationMigrator organizationMigrator,
    ILogger<ProviderMigrator> logger,
    IMigrationTrackerCache migrationTrackerCache,
    IOrganizationRepository organizationRepository,
    IPaymentService paymentService,
    IProviderBillingService providerBillingService,
    IProviderOrganizationRepository providerOrganizationRepository,
    IProviderRepository providerRepository,
    IProviderPlanRepository providerPlanRepository,
    IStripeAdapter stripeAdapter) : IProviderMigrator
{
    public async Task Migrate(Guid providerId)
    {
        var provider = await GetProviderAsync(providerId);

        if (provider == null)
        {
            return;
        }

        logger.LogInformation("CB: Starting migration for provider ({ProviderID})", providerId);

        await migrationTrackerCache.StartTracker(provider);

        await MigrateClientsAsync(providerId);

        await ConfigureTeamsPlanAsync(providerId);

        await ConfigureEnterprisePlanAsync(providerId);

        await SetupCustomerAsync(provider);

        await SetupSubscriptionAsync(provider);

        await ApplyCreditAsync(provider);

        await UpdateProviderAsync(provider);
    }

    public async Task<ProviderMigrationResult> GetResult(Guid providerId)
    {
        var providerTracker = await migrationTrackerCache.GetTracker(providerId);

        if (providerTracker == null)
        {
            return null;
        }

        var clientTrackers = await Task.WhenAll(providerTracker.OrganizationIds.Select(organizationId =>
            migrationTrackerCache.GetTracker(providerId, organizationId)));

        var migrationRecordLookup = new Dictionary<Guid, ClientOrganizationMigrationRecord>();

        foreach (var clientTracker in clientTrackers)
        {
            var migrationRecord =
                await clientOrganizationMigrationRecordRepository.GetByOrganizationId(clientTracker.OrganizationId);

            migrationRecordLookup.Add(clientTracker.OrganizationId, migrationRecord);
        }

        return new ProviderMigrationResult
        {
            ProviderId = providerTracker.ProviderId,
            ProviderName = providerTracker.ProviderName,
            Result = providerTracker.Progress.ToString(),
            Clients = clientTrackers.Select(tracker =>
            {
                var foundMigrationRecord = migrationRecordLookup.TryGetValue(tracker.OrganizationId, out var migrationRecord);
                return new ClientMigrationResult
                {
                    OrganizationId = tracker.OrganizationId,
                    OrganizationName = tracker.OrganizationName,
                    Result = tracker.Progress.ToString(),
                    PreviousState = foundMigrationRecord ? new ClientPreviousState(migrationRecord) : null
                };
            }).ToList(),
        };
    }

    #region Steps

    private async Task MigrateClientsAsync(Guid providerId)
    {
        logger.LogInformation("CB: Migrating clients for provider ({ProviderID})", providerId);

        var organizations = await GetEnabledClientsAsync(providerId);

        var organizationIds = organizations.Select(organization => organization.Id);

        await migrationTrackerCache.SetOrganizationIds(providerId, organizationIds);

        foreach (var organization in organizations)
        {
            var tracker = await migrationTrackerCache.GetTracker(providerId, organization.Id);

            if (tracker is not { Progress: ClientMigrationProgress.Completed })
            {
                await organizationMigrator.Migrate(providerId, organization);
            }
        }

        logger.LogInformation("CB: Migrated clients for provider ({ProviderID})", providerId);

        await migrationTrackerCache.UpdateTrackingStatus(providerId,
            ProviderMigrationProgress.ClientsMigrated);
    }

    private async Task ConfigureTeamsPlanAsync(Guid providerId)
    {
        logger.LogInformation("CB: Configuring Teams plan for provider ({ProviderID})", providerId);

        var organizations = await GetEnabledClientsAsync(providerId);

        var teamsSeats = organizations
            .Where(IsTeams)
            .Sum(client => client.Seats) ?? 0;

        var teamsProviderPlan = (await providerPlanRepository.GetByProviderId(providerId))
            .FirstOrDefault(providerPlan => providerPlan.PlanType == PlanType.TeamsMonthly);

        if (teamsProviderPlan == null)
        {
            await providerPlanRepository.CreateAsync(new ProviderPlan
            {
                ProviderId = providerId,
                PlanType = PlanType.TeamsMonthly,
                SeatMinimum = teamsSeats,
                PurchasedSeats = 0,
                AllocatedSeats = teamsSeats
            });

            logger.LogInformation("CB: Created Teams plan for provider ({ProviderID}) with a seat minimum of {Seats}",
                providerId, teamsSeats);
        }
        else
        {
            logger.LogInformation("CB: Teams plan already exists for provider ({ProviderID}), updating seat minimum", providerId);

            teamsProviderPlan.SeatMinimum = teamsSeats;
            teamsProviderPlan.AllocatedSeats = teamsSeats;

            await providerPlanRepository.ReplaceAsync(teamsProviderPlan);

            logger.LogInformation("CB: Updated Teams plan for provider ({ProviderID}) to seat minimum of {Seats}",
                providerId, teamsProviderPlan.SeatMinimum);
        }

        await migrationTrackerCache.UpdateTrackingStatus(providerId, ProviderMigrationProgress.TeamsPlanConfigured);
    }

    private async Task ConfigureEnterprisePlanAsync(Guid providerId)
    {
        logger.LogInformation("CB: Configuring Enterprise plan for provider ({ProviderID})", providerId);

        var organizations = await GetEnabledClientsAsync(providerId);

        var enterpriseSeats = organizations
            .Where(IsEnterprise)
            .Sum(client => client.Seats) ?? 0;

        var enterpriseProviderPlan = (await providerPlanRepository.GetByProviderId(providerId))
            .FirstOrDefault(providerPlan => providerPlan.PlanType == PlanType.EnterpriseMonthly);

        if (enterpriseProviderPlan == null)
        {
            await providerPlanRepository.CreateAsync(new ProviderPlan
            {
                ProviderId = providerId,
                PlanType = PlanType.EnterpriseMonthly,
                SeatMinimum = enterpriseSeats,
                PurchasedSeats = 0,
                AllocatedSeats = enterpriseSeats
            });

            logger.LogInformation("CB: Created Enterprise plan for provider ({ProviderID}) with a seat minimum of {Seats}",
                providerId, enterpriseSeats);
        }
        else
        {
            logger.LogInformation("CB: Enterprise plan already exists for provider ({ProviderID}), updating seat minimum", providerId);

            enterpriseProviderPlan.SeatMinimum = enterpriseSeats;
            enterpriseProviderPlan.AllocatedSeats = enterpriseSeats;

            await providerPlanRepository.ReplaceAsync(enterpriseProviderPlan);

            logger.LogInformation("CB: Updated Enterprise plan for provider ({ProviderID}) to seat minimum of {Seats}",
                providerId, enterpriseProviderPlan.SeatMinimum);
        }

        await migrationTrackerCache.UpdateTrackingStatus(providerId, ProviderMigrationProgress.EnterprisePlanConfigured);
    }

    private async Task SetupCustomerAsync(Provider provider)
    {
        if (string.IsNullOrEmpty(provider.GatewayCustomerId))
        {
            var organizations = await GetEnabledClientsAsync(provider.Id);

            var sampleOrganization = organizations.FirstOrDefault(organization => !string.IsNullOrEmpty(organization.GatewayCustomerId));

            if (sampleOrganization == null)
            {
                logger.LogInformation(
                    "CB: Could not find sample organization for provider ({ProviderID}) that has a Stripe customer",
                    provider.Id);

                return;
            }

            var taxInfo = await paymentService.GetTaxInfoAsync(sampleOrganization);

            var customer = await providerBillingService.SetupCustomer(provider, taxInfo);

            await stripeAdapter.CustomerUpdateAsync(customer.Id, new CustomerUpdateOptions
            {
                Coupon = StripeConstants.CouponIDs.MSPDiscount35
            });

            provider.GatewayCustomerId = customer.Id;

            await providerRepository.ReplaceAsync(provider);

            logger.LogInformation("CB: Setup Stripe customer for provider ({ProviderID})", provider.Id);
        }
        else
        {
            logger.LogInformation("CB: Stripe customer already exists for provider ({ProviderID})", provider.Id);
        }

        await migrationTrackerCache.UpdateTrackingStatus(provider.Id, ProviderMigrationProgress.CustomerSetup);
    }

    private async Task SetupSubscriptionAsync(Provider provider)
    {
        if (string.IsNullOrEmpty(provider.GatewaySubscriptionId))
        {
            if (!string.IsNullOrEmpty(provider.GatewayCustomerId))
            {
                var subscription = await providerBillingService.SetupSubscription(provider);

                provider.GatewaySubscriptionId = subscription.Id;

                await providerRepository.ReplaceAsync(provider);

                logger.LogInformation("CB: Setup Stripe subscription for provider ({ProviderID})", provider.Id);
            }
            else
            {
                logger.LogInformation(
                    "CB: Could not set up Stripe subscription for provider ({ProviderID}) with no Stripe customer",
                    provider.Id);

                return;
            }
        }
        else
        {
            logger.LogInformation("CB: Stripe subscription already exists for provider ({ProviderID})", provider.Id);

            var providerPlans = await providerPlanRepository.GetByProviderId(provider.Id);

            var enterpriseSeatMinimum = providerPlans
                .FirstOrDefault(providerPlan => providerPlan.PlanType == PlanType.EnterpriseMonthly)?
                .SeatMinimum ?? 0;

            var teamsSeatMinimum = providerPlans
                .FirstOrDefault(providerPlan => providerPlan.PlanType == PlanType.TeamsMonthly)?
                .SeatMinimum ?? 0;

            await providerBillingService.UpdateSeatMinimums(provider, enterpriseSeatMinimum, teamsSeatMinimum);

            logger.LogInformation(
                "CB: Updated Stripe subscription for provider ({ProviderID}) with current seat minimums", provider.Id);
        }

        await migrationTrackerCache.UpdateTrackingStatus(provider.Id, ProviderMigrationProgress.SubscriptionSetup);
    }

    private async Task ApplyCreditAsync(Provider provider)
    {
        var organizations = await GetEnabledClientsAsync(provider.Id);

        var organizationCustomers =
            await Task.WhenAll(organizations.Select(organization => stripeAdapter.CustomerGetAsync(organization.GatewayCustomerId)));

        var organizationCancellationCredit = organizationCustomers.Sum(customer => customer.Balance);

        var legacyOrganizations = organizations.Where(organization =>
            organization.PlanType is
                PlanType.EnterpriseAnnually2020 or
                PlanType.EnterpriseMonthly2020 or
                PlanType.TeamsAnnually2020 or
                PlanType.TeamsMonthly2020);

        var legacyOrganizationCredit = legacyOrganizations.Sum(organization => organization.Seats ?? 0);

        await stripeAdapter.CustomerUpdateAsync(provider.GatewayCustomerId, new CustomerUpdateOptions
        {
            Balance = organizationCancellationCredit + legacyOrganizationCredit
        });

        logger.LogInformation("CB: Applied {Credit} credit to provider ({ProviderID})", organizationCancellationCredit, provider.Id);

        await migrationTrackerCache.UpdateTrackingStatus(provider.Id, ProviderMigrationProgress.CreditApplied);
    }

    private async Task UpdateProviderAsync(Provider provider)
    {
        provider.Status = ProviderStatusType.Billable;

        await providerRepository.ReplaceAsync(provider);

        logger.LogInformation("CB: Completed migration for provider ({ProviderID})", provider.Id);

        await migrationTrackerCache.UpdateTrackingStatus(provider.Id, ProviderMigrationProgress.Completed);
    }

    #endregion

    #region Utilities

    private async Task<List<Organization>> GetEnabledClientsAsync(Guid providerId)
    {
        var providerOrganizations = await providerOrganizationRepository.GetManyDetailsByProviderAsync(providerId);

        return (await Task.WhenAll(providerOrganizations.Select(providerOrganization =>
                organizationRepository.GetByIdAsync(providerOrganization.OrganizationId))))
            .Where(organization => organization.Enabled)
            .ToList();
    }

    private async Task<Provider> GetProviderAsync(Guid providerId)
    {
        var provider = await providerRepository.GetByIdAsync(providerId);

        if (provider == null)
        {
            logger.LogWarning("CB: Cannot migrate provider ({ProviderID}) as it does not exist", providerId);

            return null;
        }

        if (provider.Type != ProviderType.Msp)
        {
            logger.LogWarning("CB: Cannot migrate provider ({ProviderID}) as it is not an MSP", providerId);

            return null;
        }

        if (provider.Status == ProviderStatusType.Created)
        {
            return provider;
        }

        logger.LogWarning("CB: Cannot migrate provider ({ProviderID}) as it is not in the 'Created' state", providerId);

        return null;
    }

    private static bool IsEnterprise(Organization organization) => organization.Plan.Contains("Enterprise");
    private static bool IsTeams(Organization organization) => organization.Plan.Contains("Teams");

    #endregion
}