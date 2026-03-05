using MediatR;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Applications.Commands;

public record RetryDlqItemCommand(string ItemId) : IRequest<bool>;

public class RetryDlqItemCommandHandler : IRequestHandler<RetryDlqItemCommand, bool>
{
    private readonly IFailureQueueRepository _dlqRepository;
    private readonly IApplicationRepository _applicationRepository;

    public RetryDlqItemCommandHandler(IFailureQueueRepository dlqRepository, IApplicationRepository applicationRepository)
    {
        _dlqRepository = dlqRepository;
        _applicationRepository = applicationRepository;
    }

    public async Task<bool> Handle(RetryDlqItemCommand request, CancellationToken cancellationToken)
    {
        var item = await _dlqRepository.GetByIdAsync(request.ItemId, cancellationToken);
        if (item == null) return false;

        if (item.EntityType == "Application")
        {
            var app = await _applicationRepository.GetByIdAsync(item.EntityId, cancellationToken);
            if (app != null)
            {
                app.Status = "Queued";
                app.UpdatedAt = DateTime.UtcNow;
                await _applicationRepository.UpdateAsync(app, cancellationToken);
            }
        }

        await _dlqRepository.RemoveAsync(request.ItemId, cancellationToken);
        return true;
    }
}
